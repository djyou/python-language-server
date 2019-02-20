﻿// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Python.Analysis.Diagnostics;
using Microsoft.Python.Analysis.Documents;
using Microsoft.Python.Core;
using Microsoft.Python.Core.Disposables;
using Microsoft.Python.Core.Idle;
using Microsoft.Python.Core.Services;
using Microsoft.Python.LanguageServer.Protocol;
using Microsoft.Python.Parsing;

namespace Microsoft.Python.LanguageServer.Diagnostics {
    internal sealed class DiagnosticsService : IDiagnosticsService, IDisposable {
        private sealed class DocumentDiagnostics {
            private DiagnosticsEntry[] _entries;

            public DiagnosticsEntry[] Entries {
                get => _entries ?? Array.Empty<DiagnosticsEntry>();
                set {
                    _entries = value;
                    Changed = true;
                }
            }
            public bool Changed { get; set; }

            public void Clear() {
                Changed = _entries.Length > 0;
                _entries = Array.Empty<DiagnosticsEntry>();
            }
        }

        private readonly Dictionary<Uri, DocumentDiagnostics> _diagnostics = new Dictionary<Uri, DocumentDiagnostics>();
        private readonly DisposableBag _disposables = DisposableBag.Create<DiagnosticsService>();
        private readonly IServiceContainer _services;
        private readonly IClientApplication _clientApp;
        private readonly object _lock = new object();
        private DiagnosticsSeverityMap _severityMap = new DiagnosticsSeverityMap();
        private IRunningDocumentTable _rdt;
        private DateTime _lastChangeTime;

        private IRunningDocumentTable Rdt => _rdt ?? (_rdt = _services.GetService<IRunningDocumentTable>());

        public DiagnosticsService(IServiceContainer services) {
            _services = services;
            _clientApp = services.GetService<IClientApplication>();

            var idleTimeService = services.GetService<IIdleTimeService>();
            if (idleTimeService != null) {
                idleTimeService.Idle += OnIdle;
                idleTimeService.Closing += OnClosing;

                _disposables
                    .Add(() => idleTimeService.Idle -= OnIdle)
                    .Add(() => idleTimeService.Idle -= OnClosing);
            }
        }

        #region IDiagnosticsService
        public IReadOnlyDictionary<Uri, IReadOnlyList<DiagnosticsEntry>> Diagnostics {
            get {
                lock (_lock) {
                    return _diagnostics.ToDictionary(kvp => kvp.Key,
                        kvp => FilterBySeverityMap(kvp.Value).ToList() as IReadOnlyList<DiagnosticsEntry>);
                }
            }
        }

        public void Replace(Uri documentUri, IEnumerable<DiagnosticsEntry> entries) {
            lock (_lock) {
                if (!_diagnostics.TryGetValue(documentUri, out var documentDiagnostics)) {
                    documentDiagnostics = new DocumentDiagnostics();
                    _diagnostics[documentUri] = documentDiagnostics;
                }
                documentDiagnostics.Entries = entries.ToArray();
                documentDiagnostics.Changed = true;
                _lastChangeTime = DateTime.Now;
            }
        }

        public void Remove(Uri documentUri) {
            lock (_lock) {
                // Before removing the document, make sure we clear its diagnostics.
                if (_diagnostics.TryGetValue(documentUri, out var d)) {
                    d.Clear();
                    PublishDiagnostics();
                    _diagnostics.Remove(documentUri);
                }
            }
        }

        public int PublishingDelay { get; set; } = 1000;

        public DiagnosticsSeverityMap DiagnosticsSeverityMap {
            get => _severityMap;
            set {
                lock (_lock) {
                    _severityMap = value;
                    foreach (var d in _diagnostics) {
                        _diagnostics[d.Key].Changed = true;
                        _lastChangeTime = DateTime.Now;
                    }
                    PublishDiagnostics();
                }
            }
        }
        #endregion

        public void Dispose() {
            _disposables.TryDispose();
            ClearAllDiagnostics();
        }

        private void OnClosing(object sender, EventArgs e) => Dispose();

        private void OnIdle(object sender, EventArgs e) {
            if ((DateTime.Now - _lastChangeTime).TotalMilliseconds > PublishingDelay) {
                ConnectToRdt();
                PublishDiagnostics();
            }
        }

        private void PublishDiagnostics() {
            var diagnostics = new List<KeyValuePair<Uri, DocumentDiagnostics>>();
            lock (_lock) {
                foreach (var d in _diagnostics) {
                    if (d.Value.Changed) {
                        diagnostics.Add(d);
                        _diagnostics[d.Key].Changed = false;
                    }
                }
            }

            foreach (var kvp in diagnostics) {
                var parameters = new PublishDiagnosticsParams {
                    uri = kvp.Key,
                    diagnostics = Rdt.GetDocument(kvp.Key)?.IsOpen == true
                            ? FilterBySeverityMap(kvp.Value).Select(ToDiagnostic).ToArray()
                            : Array.Empty<Diagnostic>()
                };
                _clientApp.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", parameters).DoNotWait();
            }
        }

        private void ClearAllDiagnostics() {
            lock (_lock) {
                _diagnostics.Clear();
            }
        }

        private static Diagnostic ToDiagnostic(DiagnosticsEntry e) {
            DiagnosticSeverity s;
            switch (e.Severity) {
                case Severity.Warning:
                    s = DiagnosticSeverity.Warning;
                    break;
                case Severity.Information:
                    s = DiagnosticSeverity.Information;
                    break;
                case Severity.Hint:
                    s = DiagnosticSeverity.Hint;
                    break;
                default:
                    s = DiagnosticSeverity.Error;
                    break;
            }

            return new Diagnostic {
                range = e.SourceSpan,
                severity = s,
                source = "Python",
                code = e.ErrorCode,
                message = e.Message,
            };
        }

        private IEnumerable<DiagnosticsEntry> FilterBySeverityMap(DocumentDiagnostics d)
           => d.Entries
                .Where(e => DiagnosticsSeverityMap.GetEffectiveSeverity(e.ErrorCode, e.Severity) != Severity.Suppressed)
                .Select(e => new DiagnosticsEntry(
                    e.Message,
                    e.SourceSpan,
                    e.ErrorCode,
                    DiagnosticsSeverityMap.GetEffectiveSeverity(e.ErrorCode, e.Severity))
                );

        private void ConnectToRdt() {
            if (_rdt == null) {
                _rdt = _services.GetService<IRunningDocumentTable>();
                if (_rdt != null) {
                    _rdt.Opened += OnOpenDocument;
                    _rdt.Closed += OnCloseDocument;

                    _disposables
                        .Add(() => _rdt.Opened -= OnOpenDocument)
                        .Add(() => _rdt.Closed -= OnCloseDocument);
                }
            }
        }

        private void OnOpenDocument(object sender, DocumentEventArgs e) {
            lock (_lock) {
                if(_diagnostics.TryGetValue(e.Document.Uri, out var d)) {
                    d.Changed = d.Entries.Length > 0;
                }
            }
        }

        private void OnCloseDocument(object sender, DocumentEventArgs e) {
            lock (_lock) {
                // Before removing the document, make sure we clear its diagnostics.
                if (_diagnostics.TryGetValue(e.Document.Uri, out var d)) {
                    d.Clear();
                    PublishDiagnostics();
                    _diagnostics.Remove(e.Document.Uri);
                }
            }
        }
    }
}