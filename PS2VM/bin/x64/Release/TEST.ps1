function Write-TestValue {
    param($Path, $Name, $Value)
    if (-not (Test-Path $Path)) {
        New-Item -Path $Path -Force | Out-Null
    }
    Set-ItemProperty -Path $Path -Name $Name -Value $Value
    return $Value
}

$base = "HKCU:\Software\Ps2VmTest"

Write-Output "=== Registry VM Test (complex) ==="

# 1. Create the key
New-Item -Path $base -Force | Out-Null
Write-Output "Created $base"

# 2. Write a batch of values using a loop over an array
$names  = @("Red", "Green", "Blue")
$values = @("FF0000", "00FF00", "0000FF")

$i = 0
foreach ($n in $names) {
    Write-TestValue $base $n $values[$i]
    $i++
}
Write-Output "Wrote $($names.Length) colour values"

# 3. Write a numeric value
Set-ItemProperty -Path $base -Name "Counter" -Value 42

# 4. Read them all back, one pipeline per value
$key = Get-Item -Path $base
$c = $key.GetValue("Counter")
Write-Output "Counter = $c"

# 5. Accumulate through a pipeline with ForEach-Object + Where-Object
$sum = 0
1..6 | ForEach-Object {
    if ($_ % 2 -eq 0) { $sum += $_ }
} | Out-Null
Write-Output "Sum of evens 1..6 = $sum"

# 6. Switch on the counter — matches fall through
switch ($c) {
    { $_ -eq 42 }  { Write-Output "Counter is the answer" }
    { $_ -gt 40 }  { Write-Output "Counter is over 40" }
    { $_ -gt 100 } { Write-Output "Counter is huge" }
    default        { Write-Output "Counter is small" }
}

# 7. Clean up and verify
Remove-Item -Path $base -Recurse -Force

if (Test-Path $base) {
    Write-Output "Cleanup FAILED"
} else {
    Write-Output "Cleanup OK"
}

Write-Output "=== Done ==="