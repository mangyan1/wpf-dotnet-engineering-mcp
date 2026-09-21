# Security Tests

Direct security-contract tests for redaction, policy evaluation, the file guard, framed IPC, and workspace configuration. All run in-process against real security types with test policies supplied by `TestFixedPolicyProvider` — no network, no host process.

Run with `dotnet test tests/EngineeringMcp.SecurityTests --configuration Release --no-build` from the repository root.

## Redaction

- Redactor_RemovesCredentialsAndMasksPii
- Redactor_PreservesDiagnosticDatesVersionsAndTargetFrameworkPaths
- Redactor_StillMasksConventionalAndInternationalPhoneNumbers
- Redactor_MasksVinPaymentCardAndLabeledIdentityData

## Screenshot classification

- ScreenshotClassification_DetectsPiiAndDefaultsOff

## Policy engine and catalog

- PolicyEngine_DefaultDeny_RejectsUnavailableCapability
- PolicyEngine_RejectsPrivilegedWithoutExplicitFlag
- PolicyEngine_PermissionDenialNamesRequiredSettingAndControlCenterAction
- ToolPolicyCatalog_WpfClickReportsInteractionPolicyAndProfile
- ToolPolicyCatalog_ProfileDenialIsExplicitAndActionable
- PolicyDiagnostics_ExplainsLockedDownDefaultWithoutExposingPaths
- PolicyValidator_RejectsPiiOffAndOpenNetwork

## File guard and IPC

- FileGuard_BlocksOutsideRootAndDenyGlob
- BoundedJsonPipeProtocol_RoundTripsAndRejectsOversizedFrame

## Child process hygiene

- ProcessEnvironmentSanitizer_RemovesNetworkRelativeAndDuplicatePathEntries
