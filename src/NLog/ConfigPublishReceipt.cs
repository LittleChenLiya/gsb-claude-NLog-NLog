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
    using System.Threading;
    using NLog.Config;

    /// <summary>
    /// Acknowledgment-receipt from <see cref="LogFactory.PublishConfiguration"/>, that tells whether
    /// the new logging configuration was successfully initialized and activated, and allows waiting
    /// for the previous logging configuration to complete its close-operation.
    /// </summary>
    public sealed class ConfigPublishReceipt
    {
        private readonly object _syncRoot = new object();
        private bool _previousConfigurationClosed;

        /// <summary>
        /// Gets whether the new logging configuration was successfully initialized and activated
        /// </summary>
        public bool Succeeded { get; }

        /// <summary>
        /// Gets the error that made the configuration-publish fail. <see langword="null"/> when <see cref="Succeeded"/> = <see langword="true"/>
        /// </summary>
        public Exception? Error { get; }

        /// <summary>
        /// Gets the logging configuration that was activated by this publish-operation. <see langword="null"/> when the publish failed.
        /// </summary>
        public LoggingConfiguration? ActivatedConfiguration { get; }

        /// <summary>
        /// Gets the logging configuration that was replaced by this publish-operation. <see langword="null"/> when the publish failed, or when no configuration was active.
        /// </summary>
        public LoggingConfiguration? PreviousConfiguration { get; }

        /// <summary>
        /// Gets whether the <see cref="PreviousConfiguration"/> has completed its close-operation,
        /// which is deferred until all its in-flight target-writes have completed.
        /// </summary>
        public bool PreviousConfigurationClosed
        {
            get
            {
                lock (_syncRoot)
                {
                    return _previousConfigurationClosed;
                }
            }
        }

        internal ConfigPublishReceipt(LoggingConfiguration activatedConfiguration, LoggingConfiguration? previousConfiguration)
        {
            Succeeded = true;
            ActivatedConfiguration = activatedConfiguration;
            PreviousConfiguration = previousConfiguration;
        }

        private ConfigPublishReceipt(Exception error)
        {
            Error = error;
            _previousConfigurationClosed = true;    // Nothing to close, since publish was rejected
        }

        internal static ConfigPublishReceipt Failed(Exception error)
        {
            return new ConfigPublishReceipt(error);
        }

        /// <summary>
        /// Waits for the <see cref="PreviousConfiguration"/> to complete its close-operation,
        /// which is deferred until all its in-flight target-writes have completed.
        /// </summary>
        /// <param name="timeout">Maximum time to wait for the close-operation</param>
        /// <returns>Whether the close-operation completed within the timeout</returns>
        public bool WaitForPreviousConfigurationClosed(TimeSpan timeout)
        {
            lock (_syncRoot)
            {
                if (!_previousConfigurationClosed)
                    Monitor.Wait(_syncRoot, timeout);
                return _previousConfigurationClosed;
            }
        }

        internal void SignalPreviousConfigurationClosed()
        {
            lock (_syncRoot)
            {
                _previousConfigurationClosed = true;
                Monitor.PulseAll(_syncRoot);
            }
        }
    }
}
