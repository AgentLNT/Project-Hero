// 兼容垫片：Unity 6000.6.2f1 默认 C# 9，且其 netstandard2.1 配置不提供
// System.Runtime.CompilerServices.IsExternalInit（.NET 5+ 才有）。
// record 的 init-only 属性需要该类型；这是社区标准 polyfill，与 UnityEngine 无关，
// 不违反 noEngineReferences 边界。仅本程序集可见即可（全部 record 声明都在 ProjectHero.Logic 内）。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
