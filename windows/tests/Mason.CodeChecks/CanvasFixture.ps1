Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form=[Windows.Forms.Form]::new()
$form.Text='Mason canvas fixture'
$form.ClientSize=[Drawing.Size]::new(400,200)
$form.FormBorderStyle='FixedSingle'
$form.MaximizeBox=$false
$script:phase=0
$script:started=$false
$form.Add_Paint({
    param($source,$event)
    $left=if($script:phase -eq 0){[Drawing.Brushes]::Red}else{[Drawing.Brushes]::Lime}
    $right=if($script:phase -eq 2){[Drawing.Brushes]::Yellow}else{[Drawing.Brushes]::Blue}
    $event.Graphics.FillRectangle($left,0,0,200,200)
    $event.Graphics.FillRectangle($right,200,0,200,200)
})
$form.Add_MouseDown({param($source,$e) $script:started=$e.X -lt 200})
$form.Add_MouseUp({param($source,$e)
    if($script:started -and $e.X -lt 200 -and $script:phase -eq 0){$script:phase=1;$form.Text='Canvas left clicked'}
    elseif($script:started -and $e.X -ge 200 -and $script:phase -eq 1){$script:phase=2;$form.Text='Canvas drag completed'}
    $script:started=$false
    $form.Refresh()
})
[void]$form.ShowDialog()
