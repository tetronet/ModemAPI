using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace ModemAPI
{
    /// <summary>
    /// An object of SFDP (System For Data Protocol) data: named fields, every one of them is either a string or a nested object.
    /// The order of the fields is kept. Not thread-safe.
    /// </summary>
    /// <remarks>
    /// The text format, one field per line (see PROTOCOLS.md); LF is written, and CRLF, LFCR, CR and LF are read:
    /// <code>
    /// Key: value
    /// Text: "a value with spaces, \"quotes\", \\, \n line feeds and \r carriage returns"
    /// Nested:
    /// --Inner: 47
    /// --Deeper:
    /// ----Lol: kek
    /// RootAgain: yup
    /// </code>
    /// There are no types on the wire: every value is a string. There are no lists.
    /// </remarks>
    public sealed class SFDPObject
    {
        /// <summary>
        /// The deepest nesting of objects: the root is level 0, its nested objects are level 1 and so on.
        /// </summary>
        public const int MaxDepth = 64;
        private readonly Dictionary<string, object> Fields = new(StringComparer.Ordinal);
        private readonly List<string> Order = [];

        /// <summary>
        /// Number of the fields.
        /// </summary>
        public int Count => Order.Count;

        /// <summary>
        /// Names of the fields, in their order.
        /// </summary>
        public IReadOnlyList<string> Keys => Order;

        /// <summary>
        /// Gets a field: a <see cref="string"/>, an <see cref="SFDPObject"/>, or <see langword="null"/>, when there is no such field.
        /// Setting <see langword="null"/> removes the field, setting a new name adds it to the end.
        /// </summary>
        /// <exception cref="ArgumentException">The name isn't valid: empty, starts with '-', or has a whitespace, control character,
        /// ':' or '"' in it; or the value isn't a string or an SFDPObject</exception>
        public object? this[string key]
        {
            get
            {
                ArgumentNullException.ThrowIfNull(key);
                return Fields.TryGetValue(key, out object? value) ? value : null;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(key);
                if (value == null)
                {
                    if (Fields.Remove(key))
                    {
                        Order.Remove(key);
                    }
                    return;
                }
                if (value is not (string or SFDPObject))
                {
                    throw new ArgumentException($"a field of an SFDP object must be a string or an SFDPObject, not {value.GetType()}", nameof(value));
                }
                string? error = SFDPFormat.KeyError(key);
                if (error != null)
                {
                    throw new ArgumentException(error, nameof(key));
                }
                if (!Fields.ContainsKey(key))
                {
                    Order.Add(key);
                }
                Fields[key] = value;
            }
        }

        /// <summary>
        /// The field as a string, or <see langword="null"/>, when there is no such field or it is a nested object.
        /// </summary>
        public string? GetString(string key) => this[key] as string;

        /// <summary>
        /// The field as a nested object, or <see langword="null"/>, when there is no such field or it is a string.
        /// </summary>
        public SFDPObject? GetObject(string key) => this[key] as SFDPObject;

        /// <summary>
        /// Parses SFDP data (without the header line).
        /// </summary>
        /// <exception cref="FormatException">The data breaks the format</exception>
        public static SFDPObject Parse(string data)
        {
            ArgumentNullException.ThrowIfNull(data);
            SFDPObject root = new();
            List<SFDPObject> stack = [root];
            List<string> lines = SFDPFormat.SplitLines(data);
            for (int n = 0; n < lines.Count; n++)
            {
                string line = lines[n];
                if (line.Length == 0)
                {
                    continue;
                }
                int i = 0;
                int depth = 0;
                while (i + 1 < line.Length && line[i] == '-' && line[i + 1] == '-')
                {
                    i += 2;
                    depth++;
                }
                if (depth > stack.Count - 1)
                {
                    throw new FormatException($"SFDP line {n + 1}: nested {depth} levels deep, but there's no object to nest it into");
                }
                string rest = line[i..];
                int colon = rest.IndexOf(':');
                if (colon < 0)
                {
                    throw new FormatException($"SFDP line {n + 1}: no ':' after the name");
                }
                string key = rest[..colon];
                string? keyError = SFDPFormat.KeyError(key);
                if (keyError != null)
                {
                    throw new FormatException($"SFDP line {n + 1}: {keyError}");
                }
                stack.RemoveRange(depth + 1, stack.Count - depth - 1);
                SFDPObject target = stack[depth];
                if (target.Fields.ContainsKey(key))
                {
                    throw new FormatException($"SFDP line {n + 1}: '{key}' is there twice");
                }
                string after = rest[(colon + 1)..];
                if (after.Length == 0)
                {
                    if (depth + 1 > MaxDepth)
                    {
                        throw new FormatException($"SFDP line {n + 1}: objects are nested deeper than {MaxDepth} levels");
                    }
                    SFDPObject child = new();
                    target.Add(key, child);
                    stack.Add(child);
                    continue;
                }
                if (after[0] != ' ')
                {
                    throw new FormatException($"SFDP line {n + 1}: ':' must be followed by one space and the value, or end the line");
                }
                target.Add(key, SFDPFormat.ParseValue(after[1..], n + 1));
            }
            return root;
        }

        /// <summary>
        /// Writes this object in the SFDP format (without the header line), the lines are separated by LF.
        /// </summary>
        /// <exception cref="InvalidOperationException">Objects are nested deeper than <see cref="MaxDepth"/> (or into themselves)</exception>
        public override string ToString()
        {
            StringBuilder sb = new();
            Write(sb, 0);
            if (sb.Length > 0)
            {
                sb.Length--;
            }
            return sb.ToString();
        }

        private void Write(StringBuilder sb, int depth)
        {
            foreach (string key in Order)
            {
                sb.Append('-', depth * 2).Append(key).Append(':');
                if (Fields[key] is SFDPObject child)
                {
                    if (depth + 1 > MaxDepth)
                    {
                        throw new InvalidOperationException($"SFDP objects are nested deeper than {MaxDepth} levels");
                    }
                    sb.Append('\n');
                    child.Write(sb, depth + 1);
                }
                else
                {
                    sb.Append(' ');
                    SFDPFormat.WriteValue(sb, (string)Fields[key]);
                    sb.Append('\n');
                }
            }
        }

        private void Add(string key, object value)
        {
            Fields.Add(key, value);
            Order.Add(key);
        }

        /// <summary>
        /// Makes an object of type <typeparamref name="T"/> out of this one: its public writable properties are set from the fields
        /// with the same names (case-sensitive), the other fields are ignored, and the properties without a field keep their default.
        /// Strings are converted with the invariant culture to strings, numbers, bool, char, DateTime and enums (by name), nested
        /// objects to classes and structs with a parameterless constructor, <c>Dictionary&lt;string, ...&gt;</c> and SFDPObject.
        /// </summary>
        /// <exception cref="FormatException">A field can't be converted to the type of its property</exception>
        /// <exception cref="NotSupportedException">The type or the type of a property isn't supported (lists, no parameterless constructor)</exception>
        public T ToObject<T>() => (T)SFDPConvert.ToType(this, typeof(T), 0);

        /// <summary>
        /// Makes an SFDP object out of any object: from its public readable properties, or the entries of a dictionary with string keys.
        /// Properties set to <see langword="null"/> are left out. Strings, numbers, bool, char, DateTime and enums become strings
        /// (invariant culture), other objects become nested objects. Lists aren't supported.
        /// </summary>
        /// <exception cref="NotSupportedException">The value is a string, a number or another single value, a list, or has a list in it</exception>
        /// <exception cref="InvalidOperationException">Objects are nested deeper than <see cref="MaxDepth"/> (or into themselves)</exception>
        /// <exception cref="ArgumentException">A key of a dictionary isn't a valid name of a field</exception>
        public static SFDPObject FromObject(object value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return SFDPConvert.FromValue(value, 0);
        }
    }

    /// <summary>
    /// The rules of names and values of the SFDP format.
    /// </summary>
    internal static class SFDPFormat
    {
        /// <summary>
        /// Length of the line break at <paramref name="i"/>: 2 for CRLF and LFCR, 1 for CR and LF, 0 when there's none.
        /// </summary>
        public static int LineBreakLength(string s, int i)
        {
            if (i >= s.Length || s[i] is not ('\r' or '\n'))
            {
                return 0;
            }
            return i + 1 < s.Length && s[i + 1] is ('\r' or '\n') && s[i + 1] != s[i] ? 2 : 1;
        }

        /// <summary>
        /// Splits the text into lines; CRLF, LFCR, CR and LF are line breaks.
        /// </summary>
        public static List<string> SplitLines(string s)
        {
            List<string> lines = [];
            int start = 0;
            int i = 0;
            while (i < s.Length)
            {
                int length = LineBreakLength(s, i);
                if (length == 0)
                {
                    i++;
                    continue;
                }
                lines.Add(s[start..i]);
                i += length;
                start = i;
            }
            lines.Add(s[start..]);
            return lines;
        }

        /// <summary>
        /// Null, when the name is valid, otherwise why it isn't.
        /// </summary>
        public static string? KeyError(string key)
        {
            if (key.Length == 0)
            {
                return "the name of a field must not be empty";
            }
            if (key[0] == '-')
            {
                return $"the name of a field must not start with '-': '{key}'";
            }
            foreach (char c in key)
            {
                if (c <= ' ' || c == '\x7f' || c == ':' || c == '"')
                {
                    return $"the name of a field must not have whitespaces, control characters, ':' or '\"' in it: '{key}'";
                }
            }
            return null;
        }

        private static bool NeedsQuotes(string value)
        {
            if (value.Length == 0)
            {
                return true;
            }
            foreach (char c in value)
            {
                if (c <= ' ' || c == '\x7f' || c == '"')
                {
                    return true;
                }
            }
            return false;
        }

        public static void WriteValue(StringBuilder sb, string value)
        {
            if (!NeedsQuotes(value))
            {
                sb.Append(value);
                return;
            }
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
        }

        public static string ParseValue(string text, int line)
        {
            if (text.Length == 0)
            {
                throw new FormatException($"SFDP line {line}: no value after ': '");
            }
            if (text[0] != '"')
            {
                if (NeedsQuotes(text))
                {
                    throw new FormatException($"SFDP line {line}: a value with whitespaces, control characters or '\"' must be in quotes");
                }
                return text;
            }
            StringBuilder sb = new(text.Length);
            int i = 1;
            while (true)
            {
                if (i >= text.Length)
                {
                    throw new FormatException($"SFDP line {line}: the quotes aren't closed");
                }
                char c = text[i];
                if (c == '"')
                {
                    if (i != text.Length - 1)
                    {
                        throw new FormatException($"SFDP line {line}: something after the closing quote");
                    }
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    if (i + 1 >= text.Length)
                    {
                        throw new FormatException($"SFDP line {line}: '\\' at the end");
                    }
                    char escaped = text[i + 1];
                    sb.Append(escaped switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        'n' => '\n',
                        'r' => '\r',
                        _ => throw new FormatException($"SFDP line {line}: '\\{escaped}' isn't an escape, only \\\", \\\\, \\n and \\r are"),
                    });
                    i += 2;
                    continue;
                }
                sb.Append(c);
                i++;
            }
        }
    }

    /// <summary>
    /// Converts SFDP objects to .NET objects and back by reflection.
    /// </summary>
    internal static class SFDPConvert
    {
        private static bool IsSingleValue(Type t) =>
            t == typeof(string) || t.IsEnum || t.IsPrimitive || t == typeof(decimal) || t == typeof(DateTime);

        private static string FormatSingleValue(object value) => value switch
        {
            DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
            Enum e => e.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };

        public static SFDPObject FromValue(object value, int depth)
        {
            if (value is SFDPObject ready)
            {
                return ready;
            }
            Type type = value.GetType();
            if (IsSingleValue(type))
            {
                throw new NotSupportedException($"SFDP data must be an object with fields, not a single {type}");
            }
            if (depth > SFDPObject.MaxDepth)
            {
                throw new InvalidOperationException($"SFDP objects are nested deeper than {SFDPObject.MaxDepth} levels");
            }
            SFDPObject result = new();
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key)
                    {
                        throw new NotSupportedException($"the keys of a dictionary in SFDP data must be strings, not {entry.Key.GetType()}");
                    }
                    SetField(result, key, entry.Value, depth);
                }
                return result;
            }
            if (value is IEnumerable)
            {
                throw new NotSupportedException($"SFDP doesn't support lists ({type})");
            }
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetGetMethod() == null)
                {
                    continue;
                }
                SetField(result, property.Name, property.GetValue(value), depth);
            }
            return result;
        }

        private static void SetField(SFDPObject target, string key, object? value, int depth)
        {
            if (value == null)
            {
                return;
            }
            target[key] = IsSingleValue(value.GetType()) ? FormatSingleValue(value) : FromValue(value, depth + 1);
        }

        public static object ToType(SFDPObject source, Type type, int depth)
        {
            if (type == typeof(SFDPObject) || type == typeof(object))
            {
                return source;
            }
            Type? valueType = DictionaryValueType(type);
            if (valueType != null)
            {
                IDictionary dictionary = (IDictionary)Activator.CreateInstance(type.IsInterface ? typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType) : type)!;
                foreach (string key in source.Keys)
                {
                    dictionary[key] = ToValue(source[key]!, valueType, key, depth);
                }
                return dictionary;
            }
            if (IsSingleValue(Nullable.GetUnderlyingType(type) ?? type))
            {
                throw new FormatException($"an SFDP object can't be converted to {type}");
            }
            if (typeof(IEnumerable).IsAssignableFrom(type))
            {
                throw new NotSupportedException($"SFDP doesn't support lists ({type})");
            }
            if (type.IsAbstract || type.IsInterface || (!type.IsValueType && type.GetConstructor(Type.EmptyTypes) == null))
            {
                throw new NotSupportedException($"{type} can't be made out of SFDP data: it needs a public parameterless constructor");
            }
            object result = Activator.CreateInstance(type)!;
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetSetMethod() == null)
                {
                    continue;
                }
                object? field = source[property.Name];
                if (field != null)
                {
                    property.SetValue(result, ToValue(field, property.PropertyType, property.Name, depth));
                }
            }
            return result;
        }

        private static Type? DictionaryValueType(Type type)
        {
            if (!type.IsGenericType)
            {
                return null;
            }
            Type definition = type.GetGenericTypeDefinition();
            Type[] arguments = type.GetGenericArguments();
            if (arguments.Length != 2 || arguments[0] != typeof(string))
            {
                return null;
            }
            if (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                return arguments[1];
            }
            return null;
        }

        private static object ToValue(object field, Type type, string key, int depth)
        {
            Type target = Nullable.GetUnderlyingType(type) ?? type;
            if (field is SFDPObject nested)
            {
                if (depth + 1 > SFDPObject.MaxDepth)
                {
                    throw new FormatException($"SFDP objects are nested deeper than {SFDPObject.MaxDepth} levels");
                }
                return ToType(nested, target, depth + 1);
            }
            string text = (string)field;
            if (target == typeof(string) || target == typeof(object))
            {
                return text;
            }
            try
            {
                if (target.IsEnum)
                {
                    return Enum.Parse(target, text, false);
                }
                if (target == typeof(DateTime))
                {
                    return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                }
                if (IsSingleValue(target))
                {
                    return Convert.ChangeType(text, target, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
            {
                throw new FormatException($"SFDP field '{key}' = '{text}' can't be converted to {target}", ex);
            }
            throw new FormatException($"SFDP field '{key}' is a string, but {target} needs an object");
        }
    }
}
