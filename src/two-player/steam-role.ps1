function Select-CoopSteamRole([string]$PreferenceFile) {
    Write-CoopLaunchProgress ([IO.Path]::GetDirectoryName($PreferenceFile)) 'role-loading-forms'
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    Write-CoopLaunchProgress ([IO.Path]::GetDirectoryName($PreferenceFile)) 'role-forms-loaded'
    [Windows.Forms.Application]::EnableVisualStyles()
    Write-CoopLaunchProgress ([IO.Path]::GetDirectoryName($PreferenceFile)) 'role-visual-styles-ready'
    $form = New-Object Windows.Forms.Form
    Write-CoopLaunchProgress ([IO.Path]::GetDirectoryName($PreferenceFile)) 'role-form-created'
    $form.Text = '影子诡局 · Shadow Gambit Co-op'
    $form.ClientSize = New-Object Drawing.Size(620,400)
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedDialog
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.StartPosition = 'CenterScreen'
    $form.ShowInTaskbar = $true
    $form.TopMost = $true
    $form.Add_Shown({ Write-CoopLaunchProgress ([IO.Path]::GetDirectoryName($PreferenceFile)) 'role-dialog-shown'; $this.Activate(); $this.BringToFront() })
    $form.Font = New-Object Drawing.Font('Microsoft YaHei UI',10)
    $label = New-Object Windows.Forms.Label
    $label.Text = '选择这次的游戏方式 / Choose how to play'
    $label.Location = New-Object Drawing.Point(24,22)
    $label.Size = New-Object Drawing.Size(572,28)
    $form.Controls.Add($label)
    $soloButton = New-Object Windows.Forms.Button
    $soloButton.Text = '单人游戏 / Single player'
    $soloButton.Location = New-Object Drawing.Point(24,64)
    $soloButton.Size = New-Object Drawing.Size(572,48)
    $soloButton.Add_Click({ $this.FindForm().Tag = 'Solo'; $this.FindForm().Close() })
    $form.Controls.Add($soloButton)
    $hostButton = New-Object Windows.Forms.Button
    $hostButton.Text = '创建房间 / Host a room'
    $hostButton.Location = New-Object Drawing.Point(24,128)
    $hostButton.Size = New-Object Drawing.Size(278,54)
    $hostButton.Add_Click({ $this.FindForm().Tag = 'Host'; $this.FindForm().Close() })
    $form.Controls.Add($hostButton)
    $guestButton = New-Object Windows.Forms.Button
    $guestButton.Text = '加入房间 / Join a room'
    $guestButton.Location = New-Object Drawing.Point(318,128)
    $guestButton.Size = New-Object Drawing.Size(278,54)
    $guestButton.Add_Click({ $this.FindForm().Tag = 'Guest'; $this.FindForm().Close() })
    $form.Controls.Add($guestButton)
    $networkPreference = Join-Path ([IO.Path]::GetDirectoryName($PreferenceFile)) 'last-network.txt'
    $networkLabel = New-Object Windows.Forms.Label
    $networkLabel.Text = '联机网络 / Network'
    $networkLabel.Location = New-Object Drawing.Point(24,210)
    $networkLabel.Size = New-Object Drawing.Size(170,28)
    $form.Controls.Add($networkLabel)
    $networkBox = New-Object Windows.Forms.ComboBox
    $networkBox.DropDownStyle = 'DropDownList'
    $networkBox.Location = New-Object Drawing.Point(202,206)
    $networkBox.Size = New-Object Drawing.Size(394,32)
    $networkOptions=@(Get-CoopNetworkOptions)
    foreach($option in $networkOptions){[void]$networkBox.Items.Add($option.Label)}
    $savedNetwork = if (Test-Path -LiteralPath $networkPreference) { [IO.File]::ReadAllText($networkPreference).Trim() } else { '' }
    if($savedNetwork -eq 'natpierce'){$savedNetwork='adapter:natpierce'}
    $networkBox.SelectedIndex=0
    for($i=0;$i -lt $networkOptions.Count;$i++){if($networkOptions[$i].Key -eq $savedNetwork){$networkBox.SelectedIndex=$i;break}}
    $form.Controls.Add($networkBox)
    $hint = New-Object Windows.Forms.Label
    $hint.Text = "局域网／组网：两端选择同一网络。
LAN / virtual LAN: select the same network on both PCs.
内网穿透：无需选择 IP，加入时填写域名和端口。
Tunnels: ignore this IP selection; enter the public host:port when joining."
    $hint.Location = New-Object Drawing.Point(24,260)
    $hint.Size = New-Object Drawing.Size(572,116)
    $hint.Font = New-Object Drawing.Font('Microsoft YaHei UI',9)
    $form.Controls.Add($hint)
    $last = if (Test-Path -LiteralPath $PreferenceFile) { [IO.File]::ReadAllText($PreferenceFile).Trim() } else { '' }
    $form.ActiveControl = if ($last -eq 'Host') { $hostButton } elseif ($last -eq 'Guest') { $guestButton } else { $soloButton }
    try {
        Write-CoopLaunchProgress ([IO.Path]::GetDirectoryName($PreferenceFile)) 'role-show-dialog'
        [void]$form.ShowDialog()
        $choice = [string]$form.Tag
        if ($choice -in @('Solo','Host','Guest')) {
            [IO.File]::WriteAllText($PreferenceFile,$choice)
            if ($choice -ne 'Solo') { [IO.File]::WriteAllText($networkPreference,[string]$networkOptions[$networkBox.SelectedIndex].Key) }
            return $choice
        }
        return ''
    } finally { $form.Dispose() }
}
