function Select-CoopJoinAddress([string]$SavedAddress, [string]$RememberedRoom,[string]$SavedUdpAddress='',[object[]]$History=@(),[bool]$SavedHttps=$false,[string]$Notice='') {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $form = New-Object Windows.Forms.Form
    $form.Text = '多人试行 · 加入房间 / Multiplayer Trial - Join'
    $form.ClientSize = New-Object Drawing.Size(680,630)
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.StartPosition = 'CenterScreen'
    $form.ShowInTaskbar = $true
    $form.TopMost = $true
    $form.Add_Shown({ $this.Activate(); $this.BringToFront() })
    $form.Font = New-Object Drawing.Font('Microsoft YaHei UI',10)
    $lan = New-Object Windows.Forms.Button
    $lan.Text = '局域网自动找房 / Find a room on the selected network'
    $lan.Location = New-Object Drawing.Point(24,20)
    $lan.Size = New-Object Drawing.Size(632,50)
    $lan.Add_Click({ $this.FindForm().Tag = @{Mode='LAN';Address='';Room=''}; $this.FindForm().Close() })
    $form.Controls.Add($lan)
    $label = New-Object Windows.Forms.Label
    $label.Text = '房间地址或邀请 / Room address or invitation
虚拟 IP、域名:端口、完整链接 / Virtual IP, host:port or full link'
    $label.Location = New-Object Drawing.Point(24,90)
    $label.Size = New-Object Drawing.Size(632,52)
    $form.Controls.Add($label)
    $inputBox = New-Object Windows.Forms.ComboBox
    $inputBox.DropDownStyle='DropDown'
    foreach($entry in $History){if($entry.Address){[void]$inputBox.Items.Add([string]$entry.Address)}}
    $inputBox.Tag=$History
    $inputBox.Name = 'address'
    $inputBox.Text = Get-CoopSavedAddress $SavedAddress
    $inputBox.MaxLength = 2048
    $inputBox.Location = New-Object Drawing.Point(24,148)
    $inputBox.Size = New-Object Drawing.Size(632,30)
    $form.Controls.Add($inputBox)
    $httpsBox = New-Object Windows.Forms.CheckBox
    $httpsBox.Name='useHttps'
    $httpsBox.Text='使用 HTTPS / Use HTTPS (optional)'
    $httpsBox.Location=New-Object Drawing.Point(24,188)
    $httpsBox.Size=New-Object Drawing.Size(632,28)
    $httpsBox.Checked=$SavedHttps
    $form.Controls.Add($httpsBox)
    $hint = New-Object Windows.Forms.Label
    $hint.Text = '默认独立 TCP，无需绑定子域名。仅在隧道启用 HTTPS 时勾选。
Raw TCP is the default. No HTTPS subdomain needed. Check only for HTTPS tunnels.'
    $hint.Location = New-Object Drawing.Point(24,224)
    $hint.Size = New-Object Drawing.Size(632,50)
    $hint.Font = New-Object Drawing.Font('Microsoft YaHei UI',9)
    $form.Controls.Add($hint)
    $join = New-Object Windows.Forms.Button
    $join.Text = '通过此地址加入
Join this address'
    $udpLabel=New-Object Windows.Forms.Label
    $udpLabel.Text='UDP 地址不同才填 / Different UDP host:port (optional)'
    $udpLabel.Location=New-Object Drawing.Point(24,290)
    $udpLabel.Size=New-Object Drawing.Size(632,28)
    $form.Controls.Add($udpLabel)
    $udpBox=New-Object Windows.Forms.TextBox
    $udpBox.Name='udpAddress';$udpBox.Text=$SavedUdpAddress;$udpBox.MaxLength=260
    $udpBox.Location=New-Object Drawing.Point(24,322);$udpBox.Size=New-Object Drawing.Size(632,30)
    $form.Controls.Add($udpBox)
    $inputBox.Add_SelectedIndexChanged({
        $selected=[string]$this.SelectedItem
        foreach($entry in $this.Tag){if($entry.Address -eq $selected){$this.FindForm().Controls['udpAddress'].Text=[string]$entry.UdpAddress;$this.FindForm().Controls['useHttps'].Checked=[bool]$entry.UseHttps;break}}
    })
    $udpHint=New-Object Windows.Forms.Label
    $udpHint.Text='隧道本地目标 / Local tunnel targets: TCP + UDP 127.0.0.1:38479; HTTPS TCP :38477
独立 TCP 模式留空自动探测 UDP / Raw TCP: blank probes UDP, with TCP fallback.'
    $udpHint.Location=New-Object Drawing.Point(24,360);$udpHint.Size=New-Object Drawing.Size(632,48)
    $udpHint.Font=New-Object Drawing.Font('Microsoft YaHei UI',9);$form.Controls.Add($udpHint)
    $join.Location = New-Object Drawing.Point(408,560)
    $join.Size = New-Object Drawing.Size(248,50)
    $join.Tag = $RememberedRoom
    $status=New-Object Windows.Forms.TextBox
    $status.Name='joinStatus';$status.Multiline=$true;$status.ReadOnly=$true;$status.TabStop=$false;$status.BorderStyle="None";$status.ScrollBars="Vertical";$status.Text=if($Notice){$Notice}else{'请房主先创建房间。连接失败后保留输入，可直接重试。
The host must create a room first. Your entries are kept if a retry is needed.'}
    $status.Location=New-Object Drawing.Point(24,420);$status.Size=New-Object Drawing.Size(632,122)
    $status.Font=New-Object Drawing.Font('Microsoft YaHei UI',9)
    $form.Controls.Add($status)
    $join.Add_Click({
        $f = $this.FindForm()
        $address = $f.Controls['address'].Text.Trim()
        $this.Enabled=$false
        $f.Controls['joinStatus'].Text='正在检查地址和房间，请稍候… / Checking the address and room…'
        $f.Refresh()
        try {
            if($address.StartsWith('SGC1 ')) {
                $paired=Redeem-CoopInvite $address
                $f.Tag=@{Mode='Tunnel';Address=$paired.Room;Room=$paired.Room;UdpAddress=$paired.UdpAddress;UseHttps=($paired.Room -match '^https://')}
                $f.Close();return
            }
            $virtualIP=$null
            if([Net.IPAddress]::TryParse($address,[ref]$virtualIP) -and (Test-CoopNetworkIPv4 $address)) {
                $f.Tag=@{Mode='DirectIP';Address=$address;Room='';RemoteAddress=$virtualIP.IPAddressToString}
                $f.Close();return
            }
            if(!$f.Controls['useHttps'].Checked) {
                $address=Set-CoopAddressScheme $address $false
                $endpoint=$null
                if(![Uri]::TryCreate($address,[UriKind]::Absolute,[ref]$endpoint) -or $endpoint.Port -lt 1 -or $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment -or $endpoint.AbsolutePath -ne '/') {throw '独立 TCP 请填域名:端口，不需要房间标识。 / Raw TCP requires host:port, without a room ID.'}
                $udp=Resolve-CoopUdpEndpoint $f.Controls['udpAddress'].Text
                $f.Tag=@{Mode='NativeTCP';Address=$address;Room='';UdpAddress=$udp;UseHttps=$f.Controls['useHttps'].Checked}
                $f.Close();return
            }
            $address = Set-CoopAddressScheme $address $f.Controls['useHttps'].Checked
            $room = Resolve-CoopRoomAddress $address ([string]$this.Tag)
            $udp=Resolve-CoopUdpEndpoint $f.Controls['udpAddress'].Text
            $f.Tag = @{Mode='Tunnel';Address=$address;Room=$room;UdpAddress=$udp;UseHttps=$f.Controls['useHttps'].Checked}
            $f.Close()
        } catch { $f.Controls['joinStatus'].Text='暂时无法连接，请检查地址，或等房主创建房间后重试。 / Cannot connect yet. Check the address and wait for the host to open a room.'+[Environment]::NewLine+($_.Exception.GetBaseException().Message) } finally { if(!$f.IsDisposed){$this.Enabled=$true} }
    })
    $form.Controls.Add($join)
    $cancel = New-Object Windows.Forms.Button
    $cancel.Text = '取消 / Cancel'
    $cancel.Location = New-Object Drawing.Point(24,560)
    $cancel.Size = New-Object Drawing.Size(160,50)
    $cancel.DialogResult = 'Cancel'
    $form.Controls.Add($cancel)
    $form.CancelButton = $cancel
    $form.AcceptButton = $join
    $form.ActiveControl = if ($SavedAddress) { $inputBox } else { $lan }
    try { [void]$form.ShowDialog(); return $form.Tag } finally { $form.Dispose() }
}
