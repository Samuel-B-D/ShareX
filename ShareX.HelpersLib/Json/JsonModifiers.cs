#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ShareX.HelpersLib.Json
{
    /// <summary>
    /// Implemented by types that decide at serialization time whether a member is written
    /// (replacement for the Newtonsoft.Json ShouldSerializeXxx() method convention).
    /// </summary>
    public interface IJsonShouldSerialize
    {
        /// <param name="memberName">CLR name of the property or field.</param>
        bool ShouldSerialize(string memberName);
    }

    /// <summary>
    /// The member is not written when it equals its [DefaultValue] (or the CLR default when there is none)
    /// (replacement for Newtonsoft.Json [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class JsonIgnoreDefaultAttribute : Attribute
    {
    }

    /// <summary>
    /// The member is always written, even with <see cref="ShareXJson.IgnoreDefaults"/>
    /// (replacement for Newtonsoft.Json [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class JsonAlwaysWriteAttribute : Attribute
    {
    }

    internal static class JsonModifiers
    {
        private const string EncryptedTag = "$DPAPIEncrypted$";

        public static void Apply(JsonTypeInfo typeInfo, bool settings, bool encrypt, bool ignoreDefaults)
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object)
            {
                return;
            }

            bool hasShouldSerialize = typeof(IJsonShouldSerialize).IsAssignableFrom(typeInfo.Type);

            for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
            {
                JsonPropertyInfo property = typeInfo.Properties[i];

                // Newtonsoft.Json settings were written with WritablePropertiesOnlyResolver.
                if (settings && property.Set == null)
                {
                    typeInfo.Properties.RemoveAt(i);
                    continue;
                }

                ICustomAttributeProvider? attributes = property.AttributeProvider;
                string memberName = (attributes as MemberInfo)?.Name ?? property.Name;

                if (property.PropertyType == typeof(string) && attributes != null && attributes.IsDefined(typeof(JsonEncryptAttribute), true))
                {
                    ApplyEncryption(property, encrypt);
                }

                if (IsEnum(property.PropertyType) && property.Set is Action<object, object?> enumSetter)
                {
                    // An unknown enum name keeps the current member value instead of failing the whole document.
                    property.Set = (target, value) =>
                    {
                        if (!LenientEnumConverterFactory.ConsumeUnknown())
                        {
                            enumSetter(target, value);
                        }
                    };
                }

                Func<object, object?, bool>? shouldSerialize = property.ShouldSerialize;

                if (attributes != null && (ignoreDefaults || attributes.IsDefined(typeof(JsonIgnoreDefaultAttribute), true)) &&
                    !attributes.IsDefined(typeof(JsonAlwaysWriteAttribute), true))
                {
                    object? defaultValue = GetDefaultValue(attributes, property.PropertyType, out bool hasDefaultValue);
                    Func<object, object?, bool>? previous = shouldSerialize;
                    shouldSerialize = (target, value) => (previous == null || previous(target, value)) && !IsDefault(value, defaultValue, hasDefaultValue);
                }

                if (hasShouldSerialize)
                {
                    Func<object, object?, bool>? previous = shouldSerialize;
                    shouldSerialize = (target, value) => (previous == null || previous(target, value)) &&
                        ((IJsonShouldSerialize)target).ShouldSerialize(memberName);
                }

                property.ShouldSerialize = shouldSerialize;
            }
        }

        private static void ApplyEncryption(JsonPropertyInfo property, bool encrypt)
        {
            Func<object, object?>? getter = property.Get;
            Action<object, object?>? setter = property.Set;

            if (encrypt && getter != null)
            {
                property.Get = target =>
                {
                    string? value = (string?)getter(target);

                    if (!string.IsNullOrEmpty(value))
                    {
                        try
                        {
                            return EncryptedTag + DPAPI.Encrypt(value);
                        }
                        catch
                        {
                        }
                    }

                    return value;
                };
            }

            if (setter != null)
            {
                property.Set = (target, value) =>
                {
                    string? text = (string?)value;

                    if (!string.IsNullOrEmpty(text) && text!.StartsWith(EncryptedTag, StringComparison.Ordinal))
                    {
                        try
                        {
                            text = DPAPI.Decrypt(text.Substring(EncryptedTag.Length));
                        }
                        catch
                        {
                            text = null;
                        }
                    }

                    setter(target, text);
                };
            }
        }

        private static bool IsEnum(Type type)
        {
            return (Nullable.GetUnderlyingType(type) ?? type).IsEnum;
        }

        private static object? GetDefaultValue(ICustomAttributeProvider attributes, Type type, out bool hasDefaultValue)
        {
            object[] defaultValues = attributes.GetCustomAttributes(typeof(DefaultValueAttribute), true);

            if (defaultValues.Length > 0)
            {
                hasDefaultValue = true;
                return ((DefaultValueAttribute)defaultValues[0]).Value;
            }

            hasDefaultValue = false;
            return null;
        }

        private static bool IsDefault(object? value, object? defaultValue, bool hasDefaultValue)
        {
            if (value == null)
            {
                return true;
            }

            if (hasDefaultValue)
            {
                if (defaultValue != null && value.GetType().IsEnum && !defaultValue.GetType().IsEnum)
                {
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture) == Convert.ToInt64(defaultValue, CultureInfo.InvariantCulture);
                }

                return Equals(value, defaultValue);
            }

            return value switch
            {
                bool b => !b,
                string => false,
                Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture) == 0,
                IConvertible c when value.GetType().IsPrimitive || value is decimal => Convert.ToDecimal(c, CultureInfo.InvariantCulture) == 0,
                DateTime d => d == default,
                Guid g => g == Guid.Empty,
                TimeSpan t => t == TimeSpan.Zero,
                _ => false
            };
        }
    }
}
