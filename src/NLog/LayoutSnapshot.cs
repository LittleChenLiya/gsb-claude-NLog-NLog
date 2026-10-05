//
// Copyright (c) 2004-2024 Jaroslaw Kowalski <jaak@jkowalski.net>, Kim Christensen, Julian Verdurmen
//
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
// * Redistributions of source code must retain the above copyright notice,
//   this list of conditions and the following disclaimer.
//
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution.
//
// * Neither the name of Jaroslaw Kowalski nor the names of its
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//

namespace NLog
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using NLog.Common;

    /// <summary>
    /// Explicit snapshot of the ambient layout-evaluation context (current thread and scope properties).
    /// </summary>
    /// <remarks>
    /// Captured together with a <see cref="LogEventInfo"/> before it is queued for deferred rendering,
    /// so a layout that is evaluated after the queueing (f.ex. by a background thread) still sees the
    /// thread- and scope-context of the original logging operation, instead of the foreign ambient
    /// context of whichever thread happens to perform the late evaluation.
    /// </remarks>
    public sealed class LayoutSnapshot
    {
        /// <summary>
        /// Managed thread-id captured from the logging thread.
        /// </summary>
        public int ManagedThreadId { get; }

        /// <summary>
        /// Thread-name captured from the logging thread (can be blank).
        /// </summary>
        public string? ThreadName { get; }

        /// <summary>
        /// Scope properties captured from the active <see cref="ScopeContext"/> of the logging thread.
        /// </summary>
        public IList<KeyValuePair<string, object?>> ScopeProperties { get; }

        private LayoutSnapshot(int managedThreadId, string? threadName, KeyValuePair<string, object?>[] scopeProperties)
        {
            ManagedThreadId = managedThreadId;
            ThreadName = threadName;
            ScopeProperties = scopeProperties;
        }

        /// <summary>
        /// Captures the ambient layout-evaluation context of the calling thread.
        /// </summary>
        /// <returns>Snapshot of the current thread and the active <see cref="ScopeContext"/> properties.</returns>
        public static LayoutSnapshot Capture()
        {
            KeyValuePair<string, object?>[] scopeProperties;
            var captured = ScopeContext.GetAllProperties();
            if (captured is ICollection<KeyValuePair<string, object?>> collection)
            {
                scopeProperties = new KeyValuePair<string, object?>[collection.Count];
                collection.CopyTo(scopeProperties, 0);
            }
            else
            {
                var list = new List<KeyValuePair<string, object?>>();
                foreach (var property in captured)
                    list.Add(property);
                scopeProperties = list.ToArray();
            }

            return new LayoutSnapshot(AsyncHelpers.GetManagedThreadId(), System.Threading.Thread.CurrentThread.Name, scopeProperties);
        }

        /// <summary>
        /// Lookup single scope property that was captured from the <see cref="ScopeContext"/>
        /// </summary>
        /// <param name="key">Name of property</param>
        /// <param name="value">When this method returns, contains the value associated with the specified key</param>
        /// <returns>Returns <see langword="true"/> when value was captured with the specified key</returns>
        /// <remarks>Scope dictionary keys are case-insensitive</remarks>
        public bool TryGetScopeProperty(string key, out object? value)
        {
            var scopeProperties = ScopeProperties;
            for (int i = 0; i < scopeProperties.Count; ++i)
            {
                if (ScopeContext.DefaultComparer.Equals(scopeProperties[i].Key, key))
                {
                    value = scopeProperties[i].Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Describes a captured value for diagnostic reporting, without calling ToString() on unknown objects.
        /// Strings and primitive values are rendered directly, everything else is reported by its type-name.
        /// </summary>
        internal static string DescribeValueForReport(object? value)
        {
            if (value is null)
                return "(null)";

            if (value is string stringValue)
                return string.Concat("\"", stringValue, "\"");

            if (value is System.IConvertible convertibleValue)
            {
                try
                {
                    return convertibleValue.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                }
                catch
                {
                    // Fall through to safe type-name reporting
                }
            }

            var valueType = value.GetType();
            return string.Concat("<", valueType.FullName ?? valueType.Name, ">");
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append("LayoutSnapshot ThreadId=").Append(ManagedThreadId);
            if (!string.IsNullOrEmpty(ThreadName))
                builder.Append(" ThreadName=").Append(ThreadName);

            var scopeProperties = ScopeProperties;
            for (int i = 0; i < scopeProperties.Count; ++i)
            {
                builder.Append(i == 0 ? " Scope: " : ", ");
                builder.Append(scopeProperties[i].Key).Append('=').Append(DescribeValueForReport(scopeProperties[i].Value));
            }

            return builder.ToString();
        }
    }
}
