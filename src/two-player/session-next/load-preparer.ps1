$ErrorActionPreference='Stop'
if (!('GuestPrepareProgress' -as [type])) {
    Add-Type -Path @((Join-Path $PSScriptRoot 'GuestSavePrepare.cs'),(Join-Path $PSScriptRoot 'GuestPrepareProgress.cs')) -ReferencedAssemblies 'System.Net.Http','System.IO.Compression','System.IO.Compression.FileSystem','System.Web.Extensions','System.Windows.Forms','System.Drawing'
}
