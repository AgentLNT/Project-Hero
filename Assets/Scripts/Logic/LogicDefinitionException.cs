using System;

namespace ProjectHero.Logic
{
    /// <summary>
    /// 纯逻辑定义/校验错误。所有非法配置必须携带稳定原因码（ErrorCode）拒绝，
    /// 不得钳制、饱和或静默修正后继续模拟。
    /// </summary>
    public sealed class LogicDefinitionException : Exception
    {
        /// <summary>稳定原因码，例如 <c>ATTACK_TIMING_OUT_OF_RANGE</c>。</summary>
        public string ErrorCode { get; }

        public LogicDefinitionException(string errorCode)
            : base(errorCode)
        {
            ErrorCode = errorCode;
        }

        public LogicDefinitionException(string errorCode, string detail)
            : base(errorCode + ": " + detail)
        {
            ErrorCode = errorCode;
        }
    }
}
