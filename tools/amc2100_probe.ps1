param(
    [string]$PortName = 'COM4',
    [int]$BaudRate = 19200,
    [byte]$SlaveAddress = 1,
    [single]$ProbeSetpoint = 100.0
)

Add-Type -AssemblyName System.IO.Ports

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

function Write-Single($Port, [byte]$Slave, [uint16]$Address, [uint16]$Value) {
    $core = [byte[]]@(
        $Slave,
        0x06,
        [byte](($Address -shr 8) -band 0xFF),
        [byte]($Address -band 0xFF),
        [byte](($Value -shr 8) -band 0xFF),
        [byte]($Value -band 0xFF)
    )
    $request = Add-Crc $core
    $Port.DiscardInBuffer()
    $Port.DiscardOutBuffer()
    $Port.Write($request, 0, $request.Length)
    return Read-Exact $Port 8
}

function Float-ToRegisters([single]$Value) {
    $bytes = [BitConverter]::GetBytes($Value)
    [Array]::Reverse($bytes)
    $high = [uint16](($bytes[0] -shl 8) -bor $bytes[1])
    $low = [uint16](($bytes[2] -shl 8) -bor $bytes[3])
    return @($high, $low)
}

function Registers-ToFloat([byte[]]$Response) {
    $bytes = [byte[]]@($Response[3], $Response[4], $Response[5], $Response[6])
    [Array]::Reverse($bytes)
    return [BitConverter]::ToSingle($bytes, 0)
}

function Write-FloatPair($Port, [byte]$Slave, [uint16]$Start, [single]$Value) {
    $registers = Float-ToRegisters $Value
    $core = [byte[]]@(
        $Slave,
        0x10,
        [byte](($Start -shr 8) -band 0xFF),
        [byte]($Start -band 0xFF),
        0x00,
        0x02,
        0x04,
        [byte](($registers[0] -shr 8) -band 0xFF),
        [byte]($registers[0] -band 0xFF),
        [byte](($registers[1] -shr 8) -band 0xFF),
        [byte]($registers[1] -band 0xFF)
    )
    $request = Add-Crc $core
    $Port.DiscardInBuffer()
    $Port.DiscardOutBuffer()
    $Port.Write($request, 0, $request.Length)
    return Read-Exact $Port 8
}

$port = New-Object System.IO.Ports.SerialPort $PortName, $BaudRate, ([System.IO.Ports.Parity]::None), 8, ([System.IO.Ports.StopBits]::One)
$port.ReadTimeout = 1200
$port.WriteTimeout = 1200
$port.Open()

try {
    $modeBefore = Read-Holding $port $SlaveAddress 11 1
    $setpointBefore = Read-Holding $port $SlaveAddress 2 2

    [void](Write-Single $port $SlaveAddress 11 1)
    [void](Write-FloatPair $port $SlaveAddress 2 $ProbeSetpoint)
    $setpointAfterProbe = Read-Holding $port $SlaveAddress 2 2

    [void](Write-FloatPair $port $SlaveAddress 2 0.0)
    $setpointAfterZero = Read-Holding $port $SlaveAddress 2 2

    $modeValue = (($modeBefore[3] -shl 8) -bor $modeBefore[4])

    Write-Output ("ModeResp={0}" -f ([BitConverter]::ToString($modeBefore)))
    Write-Output ("ModeValue={0}" -f $modeValue)
    Write-Output ("SetpointBeforeResp={0}" -f ([BitConverter]::ToString($setpointBefore)))
    Write-Output ("SetpointBefore={0}" -f (Registers-ToFloat $setpointBefore))
    Write-Output ("SetpointAfterProbeResp={0}" -f ([BitConverter]::ToString($setpointAfterProbe)))
    Write-Output ("SetpointAfterProbe={0}" -f (Registers-ToFloat $setpointAfterProbe))
    Write-Output ("SetpointAfterZeroResp={0}" -f ([BitConverter]::ToString($setpointAfterZero)))
    Write-Output ("SetpointAfterZero={0}" -f (Registers-ToFloat $setpointAfterZero))
}
finally {
    $port.Close()
}
