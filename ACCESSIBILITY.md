# RDCM accessibility

RDCM aims to make connection management usable with keyboard navigation and Windows accessibility settings. RDCM supports 64-bit Windows 10 and 11 with the .NET 10 Desktop Runtime and the Windows Remote Desktop control. [System requirements](README.md#install)

## Using RDCM

- Press `F1` for keyboard shortcuts or `Ctrl+Shift+P` for the command palette.
- RDCM respects Windows High Contrast and adapts its interface to each monitor’s display scaling. [Display scaling](README.md#monitors-with-different-scaling)
- Use **Focus remote desktop** in the session tab menu to send keyboard input to the remote desktop. The session toolbar’s **Release keyboard** control returns focus to RDCM.

## Known limitations

Windows provides the embedded remote desktop and the sign-in prompt. Keyboard and assistive technology behaviour inside a session depends on the Windows Remote Desktop control, the remote host and the software running there.

## Report an accessibility issue

Include your RDCM and Windows versions, what you were trying to do, and how you were navigating. If relevant, mention assistive technology, High Contrast or display scaling. Tell us where the problem occurred in RDCM, the Windows sign-in prompt or the remote desktop. [Open a bug report](https://github.com/behnamfaghih/RDCM/issues/new?template=bug_report.yml)
