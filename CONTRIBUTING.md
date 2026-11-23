# Contributing to RDCM

## Report a bug or suggest a feature

Check for an existing issue before opening a new one. [RDCM issues](https://github.com/behnamfaghih/RDCM/issues)

For a bug, include the RDCM and Windows versions, steps to reproduce it, what you expected and what happened. For a connection failure, include the **Copy details** output if available. Redact credentials, hostnames, IP addresses and usernames from logs and screenshots. Use sample data if you need to share a connection profile.

For a feature request, describe what you’re trying to do and where RDCM falls short.

## Build and test

Build RDCM on Windows with the .NET 10 SDK and the Remote Desktop ActiveX control included with Windows. [Build instructions](README.md#build-and-run-from-source)

For code changes, run the existing checks in PowerShell from the repository root.

```powershell
dotnet run --project .\RDCM.Tests\RDCM.Tests.csproj
```

The automated checks don’t require an RDP host. [Verification](README.md#verification)

## Open a pull request

Explain what changed and why, link any related issues and list the checks you ran. For changes to RDP connections or the installer, test the affected workflow on Windows where possible and note anything you couldn’t verify.

Follow the project’s code of conduct in issues, pull requests and reviews. [RDCM Code of Conduct](CODE_OF_CONDUCT.md)
