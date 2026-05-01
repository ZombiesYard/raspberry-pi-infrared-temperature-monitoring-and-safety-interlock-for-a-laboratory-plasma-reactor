param(
    [string]$PortName = 'COM4',
    [int]$BaudRate = 19200,
    [byte]$SlaveAddress = 1
)

$ErrorActionPreference = 'Stop'

function Get-Crc([byte[]]$Data) {
    $crc = 0xFFFF
    foreach ($b in $Data) {
        $crc = $crc -bxor $b
        for ($i = 0; $i -lt 8; $i++) {
            $lsb = ($crc -band 1)
            $crc = $crc -shr 1
            if ($lsb -ne 0) {
                $crc = $crc -bxor 0xA001
            }
        }
    }
    return [uint16]$crc
}

function Add-Crc([byte[]]$Frame) {
    $crc = Get-Crc $Frame
    $buffer = New-Object byte[] ($Frame.Length + 2)
    [Array]::Copy($Frame, $buffer, $Frame.Length)
    $buffer[$Frame.Length] = [byte]($crc -band 0xFF)
    $buffer[$Frame.Length + 1] = [byte](($crc -shr 8) -band 0xFF)
    return $buffer
}

function Read-Exact($Port, [int]$Length) {
    $buffer = New-Object byte[] $Length
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Port.Read($buffer, $offset, $Length - $offset)
        if ($read -le 0) {
            throw "Serial timeout after $offset of $Length bytes."
        }
        $offset += $read
    }
    return $buffer
}

function Read-Holding($Port, [byte]$Slave, [uint16]$Start, [uint16]$Count) {
    $core = [byte[]]@(
        $Slave,
        0x03,
        [byte](($Start -shr 8) -band 0xFF),
        [byte]($Start -band 0xFF),
        [byte](($Count -shr 8) -band 0xFF),
        [byte]($Count -band 0xFF)
    )
    $request = Add-Crc $core
    $Port.DiscardInBuffer()
    $Port.DiscardOutBuffer()
    $Port.Write($request, 0, $request.Length)
    return Read-Exact $Port (5 + (2 * $Count))
}

function Registers-ToFloat([byte[]]$Response) {
    $bytes = [byte[]]@($Response[3], $Response[4], $Response[5], $Response[6])
    [Array]::Reverse($bytes)
    return [BitConverter]::ToSingle($bytes, 0)
}

$port = New-Object System.IO.Ports.SerialPort $PortName, $BaudRate, ([System.IO.Ports.Parity]::None), 8, ([System.IO.Ports.StopBits]::One)
$port.ReadTimeout = 1200
$port.WriteTimeout = 1200
$port.Open()

try {
    $actualResp = Read-Holding $port $SlaveAddress 0 2
    $setResp = Read-Holding $port $SlaveAddress 2 2
    $modeResp = Read-Holding $port $SlaveAddress 11 1

    $modeValue = (($modeResp[3] -shl 8) -bor $modeResp[4])

    Write-Output ("ActualResp={0}" -f ([BitConverter]::ToString($actualResp)))
    Write-Output ("ActualFloatGuess={0}" -f (Registers-ToFloat $actualResp))
    Write-Output ("SetpointResp={0}" -f ([BitConverter]::ToString($setResp)))
    Write-Output ("SetpointFloatGuess={0}" -f (Registers-ToFloat $setResp))
    Write-Output ("ModeResp={0}" -f ([BitConverter]::ToString($modeResp)))
    Write-Output ("ModeValue={0}" -f $modeValue)
}
finally {
    $port.Close()
}
