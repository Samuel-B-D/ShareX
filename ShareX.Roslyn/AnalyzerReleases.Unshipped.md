; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SXR001 | ShareX.DeepCopy | Warning | DeepCopyGenerator, deep copyable type must be partial
SXR002 | ShareX.DeepCopy | Warning | DeepCopyGenerator, member is shared by deep copies
SXR003 | ShareX.DeepCopy | Warning | DeepCopyGenerator, read-only member cannot be deep copied
