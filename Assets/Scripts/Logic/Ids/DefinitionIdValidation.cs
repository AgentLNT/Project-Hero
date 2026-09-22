using System.Collections.Generic;

namespace ProjectHero.Logic.Ids
{
    /// <summary>
    /// 集中 ID 校验器（任务 02 工作步骤 8）。配置 ID 格式固定为：
    /// 非空、小写英文字母（a-z）、数字（0-9）、点与下划线。
    /// 禁止把显示名、GameObject 名、资源加载顺序或 Unity 实例 ID 转换成 Logic ID；
    /// ID 值类型本身保持哑构造，格式与重复校验全部收敛在这里。
    /// </summary>
    public static class DefinitionIdValidation
    {
        public const string ID_EMPTY = "ID_EMPTY";
        public const string ID_INVALID_CHARACTERS = "ID_INVALID_CHARACTERS";
        public const string ID_DUPLICATE = "ID_DUPLICATE";

        /// <summary>格式合法：非空且每个字符属于 [a-z0-9._]。</summary>
        public static bool IsValidFormat(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            foreach (char c in id)
            {
                if (!IsAllowedCharacter(c)) return false;
            }
            return true;
        }

        /// <summary>返回首个校验错误码（null = 合法）。空/空白返回 <see cref="ID_EMPTY"/>。</summary>
        public static string ValidateFormat(string id)
        {
            if (string.IsNullOrEmpty(id)) return ID_EMPTY;
            return IsValidFormat(id) ? null : ID_INVALID_CHARACTERS;
        }

        /// <summary>同一命名空间内不得重复；返回 <see cref="ID_DUPLICATE"/> 或 null。</summary>
        public static string ValidateUnique(IEnumerable<string> ids)
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (id == null) return ID_EMPTY;
                if (!seen.Add(id)) return ID_DUPLICATE;
            }
            return null;
        }

        private static bool IsAllowedCharacter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_';
        }
    }
}
