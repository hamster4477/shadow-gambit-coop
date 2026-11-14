Option Explicit
Dim fs, shell, root, ps, bridge, folder, file, writer, i, payload, command, result
Set fs = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
root = fs.GetParentFolderName(WScript.ScriptFullName)
bridge = root & "\SteamLauncherHost.exe"
ps = shell.ExpandEnvironmentStrings("%WINDIR%") & "\System32\WindowsPowerShell\v1.0\powershell.exe"
command = Quote(ps) & " -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File " & Quote(root & "\steam-start.ps1")
' GUI bridge creates PowerShell without a console. Fallback supports older packs.
If WScript.Arguments.Count = 0 Then
    If fs.FileExists(bridge) Then
        result = shell.Run(Quote(bridge) & " Guest --setup", 1, True)
    Else
        result = shell.Run(command & " -Setup", 1, True)
    End If
    WScript.Quit result
End If
If WScript.Arguments.Count > 64 Then WScript.Quit 2
folder = root & "\session-next"
If Not fs.FolderExists(folder) Then fs.CreateFolder folder
folder = folder & "\runtime"
If Not fs.FolderExists(folder) Then fs.CreateFolder folder
folder = folder & "\steam-launch"
If Not fs.FolderExists(folder) Then fs.CreateFolder folder
file = folder & "\launch-" & fs.GetTempName & ".json"
payload = "["
For i = 0 To WScript.Arguments.Count - 1
    If i > 0 Then payload = payload & ","
    payload = payload & JsonString(WScript.Arguments(i))
Next
payload = payload & "]"
Set writer = fs.CreateTextFile(file, False, True)
writer.Write payload
writer.Close
If fs.FileExists(bridge) Then
    result = shell.Run(Quote(bridge) & " Guest " & Quote(file), 1, True)
Else
    result = shell.Run(command & " -ArgumentsFile " & Quote(file), 1, True)
End If
If fs.FileExists(file) Then fs.DeleteFile file
WScript.Quit result

Function Quote(value)
    Quote = Chr(34) & value & Chr(34)
End Function

Function JsonString(value)
    Dim n, ch, code, escaped
    escaped = ""
    For n = 1 To Len(value)
        ch = Mid(value, n, 1)
        code = AscW(ch)
        If code = 34 Then
            escaped = escaped & "\" & Chr(34)
        ElseIf code = 92 Then
            escaped = escaped & "\\"
        ElseIf code >= 0 And code < 32 Then
            escaped = escaped & "\u" & Right("0000" & Hex(code), 4)
        Else
            escaped = escaped & ch
        End If
    Next
    JsonString = Chr(34) & escaped & Chr(34)
End Function
