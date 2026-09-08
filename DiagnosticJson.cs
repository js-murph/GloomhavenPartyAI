using System.Globalization;
using System.Text;

namespace GloomhavenPartyAI
{
    // Deliberately accepts only JSON primitives and other objects, never arbitrary game objects.
    internal sealed class DiagnosticJson
    {
        private readonly StringBuilder _text = new StringBuilder("{");

        internal DiagnosticJson Add(string key, string value)
        {
            Key(key);
            Quote(_text, value);
            return this;
        }

        internal DiagnosticJson Add(string key, decimal value)
        {
            Key(key);
            _text.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        internal bool TryAddNumber(string key, string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                double.IsNaN(number) || double.IsInfinity(number)) return false;
            if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal exact) &&
                (exact != 0 || number == 0)) Add(key, exact);
            else
            {
                Key(key);
                _text.Append(number.ToString("R", CultureInfo.InvariantCulture));
            }
            return true;
        }

        internal DiagnosticJson Add(string key, bool value)
        {
            Key(key);
            _text.Append(value ? "true" : "false");
            return this;
        }

        internal DiagnosticJson Add(string key, DiagnosticJson value)
        {
            Key(key);
            _text.Append(value == null ? "null" : value.ToString());
            return this;
        }

        private void Key(string key)
        {
            if (_text.Length > 1) _text.Append(',');
            Quote(_text, key);
            _text.Append(':');
        }

        private static void Quote(StringBuilder text, string value)
        {
            if (value == null)
            {
                text.Append("null");
                return;
            }
            text.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if ((char.IsHighSurrogate(c) && (i + 1 == value.Length || !char.IsLowSurrogate(value[i + 1]))) ||
                    (char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(value[i - 1]))))
                    c = '\ufffd';
                if (c == '"' || c == '\\') text.Append('\\').Append(c);
                else if (c < 32 || c > 126)
                    text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            text.Append('"');
        }

        public override string ToString()
        {
            return _text.ToString() + "}";
        }
    }
}
