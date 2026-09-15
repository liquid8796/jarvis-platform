# Jarvis Code IDE Bridge

This extension connects the open local VS Code workspace to Jarvis Code. Run
`/ide install` in Jarvis Code, then open or reload the workspace in VS Code.
The bridge activates only in trusted workspaces. Existing extensions and editor
settings are preserved; installation is not synchronized to other devices.

The connection uses a local named pipe and a random per-process authentication
token kept in the user's `.jarvis/ide` directory. Jarvis verifies the editor
process before using it. No TCP port is opened and no account sign-in is needed.

`/ide` lists matching editors, `/ide use <id>` chooses one when the same folder
is open in several windows, and `/ide selection` and `/ide diagnostics` read
the editor's current state. The diagnostics tool is available to a Code session
only while a matching editor is running. The selected text can accompany the
next coding prompt as editor context.

Diffs use virtual documents. Accept Change applies and saves the proposed text
only after a user invokes that editor command; Reject Change and closing the
tab leave the file unchanged. A change is refused if the file changed since
the review was opened. All file operations are restricted to this workspace.
