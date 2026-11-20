Option Explicit

Dim shell, files, root, windhawk, app, data, logPath, windhawkStarted, appStarted
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
root = files.GetParentFolderName(files.GetParentFolderName(WScript.ScriptFullName))
windhawk = files.BuildPath(root, ".tools\windhawk\windhawk.exe")
app = files.BuildPath(root, "dist\AITaskbar.exe")
data = files.BuildPath(shell.ExpandEnvironmentStrings("%LOCALAPPDATA%"), "AITaskbar")
logPath = files.BuildPath(data, "startup.log")

WriteLog "Launcher started"
shell.CurrentDirectory = root
windhawkStarted = StartProgram("Windhawk", windhawk, "-tray-only")
appStarted = StartProgram("AITaskbar", app, "--startup --compare")
If Not (windhawkStarted And appStarted) Then WScript.Quit 1
WScript.Quit 0

Function StartProgram(label, executable, arguments)
    StartProgram = False
    If Not files.FileExists(executable) Then
        WriteLog label & ": executable missing"
        Exit Function
    End If
    On Error Resume Next
    shell.Run """" & executable & """ " & arguments, 0, False
    If Err.Number <> 0 Then
        WriteLog label & ": launch failed (" & Err.Number & ") " & Err.Description
        Exit Function
    End If
    StartProgram = True
    WriteLog label & ": launch requested"
End Function

Sub WriteLog(message)
    On Error Resume Next
    If Not files.FolderExists(data) Then files.CreateFolder data
    Dim log
    Set log = files.OpenTextFile(logPath, 8, True)
    log.WriteLine Now & " " & message
    log.Close
End Sub
