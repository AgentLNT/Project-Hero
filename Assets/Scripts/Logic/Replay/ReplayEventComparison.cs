using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using ProjectHero.Logic.Events;

namespace ProjectHero.Logic.Replay
{
    /// <summary>Developer diagnostics compare every public payload field, including nested lists and IDs.</summary>
    public static class ReplayEventComparison
    {
        private static readonly Dictionary<Type, PropertyInfo[]> Properties = new Dictionary<Type, PropertyInfo[]>();

        public static string Canonical(LogicEvent value)
        {
            if (value is StoredReplayEvent stored) return stored.CanonicalPayload;
            return CanonicalValue(value);
        }

        internal static string CanonicalValue(object value)
        {
            var text = new StringBuilder();
            Append(text, value);
            return text.ToString();
        }

        private static void Append(StringBuilder text, object value)
        {
            if (value == null) { text.Append("null;"); return; }
            Type type = value.GetType();
            if (value is string s) { text.Append(s.Length).Append(':').Append(s).Append(';'); return; }
            if (type.IsEnum) { text.Append(type.FullName).Append(':').Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture)).Append(';'); return; }
            if (value is float f) { text.Append(BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString(CultureInfo.InvariantCulture)).Append(';'); return; }
            if (value is double d) { text.Append(BitConverter.DoubleToInt64Bits(d).ToString(CultureInfo.InvariantCulture)).Append(';'); return; }
            if (type.IsPrimitive || value is decimal)
            { text.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(';'); return; }
            if (value is IDictionary) throw new LogicDefinitionException("REPLAY_EVENT_UNORDERED_PAYLOAD", type.FullName);
            if (value is IEnumerable list)
            {
                text.Append('[');
                foreach (var item in list) Append(text, item);
                text.Append(']'); return;
            }
            if (type.Assembly != typeof(LogicEvent).Assembly)
                throw new LogicDefinitionException("REPLAY_EVENT_UNSUPPORTED_PAYLOAD", type.FullName);
            PropertyInfo[] properties;
            lock (Properties)
            {
                if (!Properties.TryGetValue(type, out properties))
                {
                    properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
                    Array.Sort(properties, (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                    Properties.Add(type, properties);
                }
            }
            text.Append(type.FullName).Append('{');
            foreach (var property in properties)
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod == null) continue;
                text.Append(property.Name).Append('=');
                Append(text, property.GetValue(value));
            }
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            Array.Sort(fields, (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            foreach (var field in fields)
            {
                text.Append(field.Name).Append('=');
                Append(text, field.GetValue(value));
            }
            text.Append('}');
        }
    }

    // File records store the canonical diagnostic payload, never a runtime type to instantiate.
    public sealed record StoredReplayEvent(long Tick, long Sequence, string CanonicalPayload) : LogicEvent(Tick, Sequence);
}
