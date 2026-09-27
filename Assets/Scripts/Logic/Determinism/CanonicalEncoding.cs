using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProjectHero.Logic.Determinism
{
    /// <summary>
    /// 规范哈希基础（主方案 3.11.2「规范编码与增量校验」）。
    ///
    /// 冻结算法：FNV-1a 64 位。摘要宽度固定 16 个十六进制字符（小写、无前缀）。
    /// 算法 ID、摘要宽度、整数大小/字节序、字符串编码、域标记与类型键全部由
    /// <see cref="ReplayFormat"/> 版本化；改变其中任何一项都必须提升
    /// <see cref="ReplayFormat.Version"/>，不得就地改写。
    ///
    /// 本类型只做字节级哈希，不理解任何业务语义：所有排序、分组与字段顺序由调用方
    /// （快照构建器 / 历史归档器）按各自冻结契约提供。
    /// </summary>
    public static class CanonicalHash
    {
        public const ulong FnvOffsetBasis = 14695981039346656037UL;
        public const ulong FnvPrime = 1099511628211UL;

        public static ulong OfBytes(byte[] bytes)
            => bytes == null ? FnvOffsetBasis : OfBytes(bytes, 0, bytes.Length);

        public static ulong OfBytes(byte[] bytes, int start, int count)
        {
            if (bytes == null) return FnvOffsetBasis;
            ulong hash = FnvOffsetBasis;
            int end = start + count;
            for (int i = start; i < end; i++)
            {
                hash ^= bytes[i];
                hash = unchecked(hash * FnvPrime);
            }
            return hash;
        }

        public static ulong Append(ulong hash, byte value)
        {
            hash ^= value;
            return unchecked(hash * FnvPrime);
        }

        /// <summary>固定宽度的规范十六进制摘要（小写，16 字符）。</summary>
        public static string ToHex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

        /// <summary>解析规范摘要；不是 16 位小写十六进制时返回 false（不抛异常）。</summary>
        public static bool TryFromHex(string hex, out ulong value)
        {
            value = 0UL;
            if (hex == null || hex.Length != 16) return false;
            for (int i = 0; i < hex.Length; i++)
            {
                char c = hex[i];
                int digit;
                if (c >= '0' && c <= '9') digit = c - '0';
                else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                else return false;
                value = (value << 4) | (uint)digit;
            }
            return true;
        }

        public static ulong FromHex(string hex)
        {
            if (!TryFromHex(hex, out ulong value))
                throw new FormatException("canonical digest must be 16 lowercase hex chars: " + (hex ?? "<null>"));
            return value;
        }
    }

    /// <summary>
    /// 域分离、长度前缀的规范字节编码器（主方案 3.11.2）。
    ///
    /// 编码契约（全部由 <see cref="ReplayFormat"/> 版本化，不得就地修改）：
    /// <list type="bullet">
    /// <item>域标记：每次 <see cref="BeginDomain"/> 先写入长度前缀字符串，因此
    /// <c>("AB","C")</c> 与 <c>("A","BC")</c> 的编码不同（域分离）。</item>
    /// <item>所有整数统一 8 字节小端（含长度前缀与集合计数），不使用变长编码，
    /// 也不存在平台相关的整数宽度。</item>
    /// <item>字符串：UTF-8 字节 + 8 字节小端长度前缀（长度是字节数不是字符数）。</item>
    /// <item>布尔：单字节 <c>0x00</c>/<c>0x01</c>。</item>
    /// <item>字节数组：8 字节小端长度前缀 + 原始字节。</item>
    /// <item>集合：先写计数再按调用方给定的稳定顺序逐项写入；编码器不排序、
    /// 不去重、不读取 <c>GetHashCode()</c>。</item>
    /// </list>
    /// 因此"同一规范输入必得同一字节序列与同一摘要"，而任何字段顺序、集合顺序或
    /// 类型键变化都会改变摘要——这正是格式版本护栏要保护的东西。
    /// </summary>
    public sealed class CanonicalEncoder
    {
        private readonly List<byte> _buffer;

        public CanonicalEncoder(int capacity = 256)
        {
            _buffer = new List<byte>(capacity < 0 ? 0 : capacity);
        }

        public int Length => _buffer.Count;

        public CanonicalEncoder BeginDomain(string domainTag) => WriteString(domainTag);

        public CanonicalEncoder WriteInt64(long value)
        {
            ulong raw = unchecked((ulong)value);
            for (int i = 0; i < 8; i++) _buffer.Add((byte)(raw >> (8 * i)));
            return this;
        }

        /// <summary>所有 32 位整数按"统一 8 字节小端"写入（冻结协议，不做窄化）。</summary>
        public CanonicalEncoder WriteInt32(int value) => WriteInt64(value);

        public CanonicalEncoder WriteUInt64(ulong value) => WriteInt64(unchecked((long)value));

        public CanonicalEncoder WriteBool(bool value)
        {
            _buffer.Add(value ? (byte)1 : (byte)0);
            return this;
        }

        public CanonicalEncoder WriteString(string value)
        {
            byte[] bytes = value == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
            WriteInt32(bytes.Length);
            _buffer.AddRange(bytes);
            return this;
        }

        public CanonicalEncoder WriteBytes(byte[] value)
        {
            if (value == null)
            {
                WriteInt32(0);
                return this;
            }
            WriteInt32(value.Length);
            _buffer.AddRange(value);
            return this;
        }

        /// <summary>集合计数（先计数、后逐项；调用方负责稳定顺序）。</summary>
        public CanonicalEncoder WriteCount(int count) => WriteInt32(count);

        public byte[] ToArray() => _buffer.ToArray();

        public ulong ToDigest() => CanonicalHash.OfBytes(_buffer.ToArray());

        public string ToDigestHex() => CanonicalHash.ToHex(ToDigest());

        /// <summary>仅供诊断：把已有字节视为完整编码。</summary>
        public static ulong DigestOf(byte[] canonicalBytes) => CanonicalHash.OfBytes(canonicalBytes);
    }
}
