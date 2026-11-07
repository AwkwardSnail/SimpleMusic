# Writes the example songs as SimpleMusic sheets (.smusic). Both melodies and
# lyrics are in the public domain. Notes are written as Pitch:Length, e.g.
# G4:e. = dotted eighth G4, r:h = half rest. Lengths: w h q e s, "." = dotted.
param([string]$Out = $PSScriptRoot)

$letters = 'CDEFGAB'
function ConvertTo-Notes([string]$melody) {
    $id = 0
    foreach ($tok in ($melody -split '\s+' | Where-Object { $_ })) {
        $pitch, $len = $tok.Split(':')
        $id++
        $note = [ordered]@{ Id = $id; Rest = $false; Dur = $len.Substring(0, 1).ToUpper(); Dot = $len.EndsWith('.'); Step = 34; Acc = 0 }
        if ($pitch -eq 'r') { $note.Rest = $true }
        else {
            $note.Step = [int]$pitch.Substring($pitch.Length - 1) * 7 + $letters.IndexOf($pitch.Substring(0, 1))
            if ($pitch.Contains('#')) { $note.Acc = 1 } elseif ($pitch.Length -eq 3 -and $pitch[1] -eq 'b') { $note.Acc = -1 }
        }
        [pscustomobject]$note
    }
}

function Save-Song([string]$file, [string]$title, [string]$lyrics, [string]$melody, [int]$beats, [int]$tempo) {
    $doc = [ordered]@{
        App = 'SimpleMusic'
        FormatVersion = 1
        Sheet = [ordered]@{
            Title = $title
            Lyrics = $lyrics
            Notes = @(ConvertTo-Notes $melody)
            Barred = $true
            Beats = $beats
            BeatTicks = 8
            FillLast = $true
            Tempo = $tempo
        }
    }
    $json = $doc | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText((Join-Path $Out $file), $json, (New-Object System.Text.UTF8Encoding $false))
    "Wrote $file"
}

# Happy Birthday starts on the last beat of a bar; a half rest fills the first bar.
Save-Song 'Happy Birthday to You.smusic' 'Happy Birthday to You' `
    "Happy birthday to you, happy birthday to you, happy birthday, dear friend, happy birthday to you!" `
    'r:h G4:e. G4:s  A4:q G4:q C5:q  B4:h G4:e. G4:s  A4:q G4:q D5:q  C5:h G4:e. G4:s
     G5:q E5:q C5:q  B4:q A4:q F5:e. F5:s  E5:q C5:q D5:q  C5:h' 3 100

Save-Song 'Twinkle Twinkle Little Star.smusic' 'Twinkle, Twinkle, Little Star' `
    "Twinkle, twinkle, little star, how I wonder what you are! Up above the world so high, like a diamond in the sky. Twinkle, twinkle, little star, how I wonder what you are!" `
    'C4:q C4:q G4:q G4:q  A4:q A4:q G4:h  F4:q F4:q E4:q E4:q  D4:q D4:q C4:h
     G4:q G4:q F4:q F4:q  E4:q E4:q D4:h  G4:q G4:q F4:q F4:q  E4:q E4:q D4:h
     C4:q C4:q G4:q G4:q  A4:q A4:q G4:h  F4:q F4:q E4:q E4:q  D4:q D4:q C4:h' 4 100
