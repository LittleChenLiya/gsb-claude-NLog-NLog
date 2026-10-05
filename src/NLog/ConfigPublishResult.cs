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
    using System;
    using NLog.Config;

    /// <summary>
    /// Represents the result of publishing a <see cref="LoggingConfiguration"/> to a <see cref="LogFactory"/>.
    /// </summary>
    /// <remarks>
    /// A failed publish (<see cref="Succeeded"/>=<see langword="false"/>) means the new configuration
    /// failed to initialize and the previously active configuration was left unchanged. Any targets that
    /// were created as part of the failed configuration have been closed.
    /// </remarks>
    public sealed class ConfigPublishResult
    {
        internal ConfigPublishResult(bool succeeded, LoggingConfiguration? previousConfiguration, Exception? exception)
        {
            Succeeded = succeeded;
            PreviousConfiguration = previousConfiguration;
            Exception = exception;
        }

        /// <summary>
        /// Gets a value indicating whether the configuration was successfully published and activated.
        /// </summary>
        /// <remarks>
        /// When <see langword="false"/>, the active <see cref="LogFactory.Configuration"/> is unchanged
        /// (still <see cref="PreviousConfiguration"/>), and <see cref="Exception"/> describes the failure.
        /// </remarks>
        public bool Succeeded { get; }

        /// <summary>
        /// Gets the previously active configuration that was replaced (if any).
        /// </summary>
        /// <remarks>
        /// When publishing succeeds, this is the configuration that was active before the publish and
        /// that will be flushed and closed in the background once its in-flight events have completed.
        /// When publishing fails, this is the configuration that remains active.
        /// </remarks>
        public LoggingConfiguration? PreviousConfiguration { get; }

        /// <summary>
        /// Gets the exception that caused the publish to fail (if any).
        /// </summary>
        public Exception? Exception { get; }
    }
}
