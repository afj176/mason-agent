Add-Type -AssemblyName System.Windows.Forms
$form=New-Object Windows.Forms.Form
$form.Text='Mason computer-use QA'
$form.Width=400
$form.Height=180
$inputBox=New-Object Windows.Forms.TextBox
$inputBox.AccessibleName='Receipt input'
$inputBox.Top=10
$button=New-Object Windows.Forms.Button
$button.Text='Record receipt'
$button.Top=42
$button.Width=160
$status=New-Object Windows.Forms.Label
$status.Text='Waiting for receipt'
$status.Top=85
$status.Width=300
$button.Add_Click({$status.Text='Recorded '+$inputBox.Text})
$form.Controls.AddRange(@($inputBox,$button,$status))
[void]$form.ShowDialog()
