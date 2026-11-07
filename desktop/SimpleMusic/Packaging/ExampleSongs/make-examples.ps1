# Writes the example songs as SimpleMusic sheets (.smusic). Both melodies and
# lyrics are in the public domain. Notes are written as Pitch:Length, e.g.
# G4:e. = dotted eighth G4, r:h = half rest. Lengths: w h q e s, "." = dotted.
# Lyrics are one syllable per sung note; "Twin-kle" puts "Twin-" and "kle" on
# two notes, and SimpleMusic draws the hyphen between them.
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
    $notes = @(ConvertTo-Notes $melody)
    $syllables = @(foreach ($word in ($lyrics -split '\s+' | Where-Object { $_ })) {
        $parts = $word.Split('-')
        for ($i = 0; $i -lt $parts.Count; $i++) { if ($i -lt $parts.Count - 1) { $parts[$i] + '-' } else { $parts[$i] } }
    })
    $sung = @($notes | Where-Object { -not $_.Rest })
    if ($sung.Count -ne $syllables.Count) { throw "$file has $($sung.Count) notes but $($syllables.Count) syllables" }
    for ($i = 0; $i -lt $sung.Count; $i++) { $sung[$i] | Add-Member Lyric $syllables[$i] }
    $doc = [ordered]@{
        App = 'SimpleMusic'
        FormatVersion = 1
        Sheet = [ordered]@{
            Title = $title
            Notes = $notes
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
    "Hap-py birth-day to you, hap-py birth-day to you, hap-py birth-day, dear some-one, hap-py birth-day to you!" `
    'r:h G4:e. G4:s  A4:q G4:q C5:q  B4:h G4:e. G4:s  A4:q G4:q D5:q  C5:h G4:e. G4:s
     G5:q E5:q C5:q  B4:q A4:q F5:e. F5:s  E5:q C5:q D5:q  C5:h' 3 100

Save-Song 'Twinkle Twinkle Little Star.smusic' 'Twinkle, Twinkle, Little Star' `
    "Twin-kle, twin-kle, lit-tle star, how I won-der what you are! Up a-bove the world so high, like a dia-mond in the sky. Twin-kle, twin-kle, lit-tle star, how I won-der what you are!" `
    'C4:q C4:q G4:q G4:q  A4:q A4:q G4:h  F4:q F4:q E4:q E4:q  D4:q D4:q C4:h
     G4:q G4:q F4:q F4:q  E4:q E4:q D4:h  G4:q G4:q F4:q F4:q  E4:q E4:q D4:h
     C4:q C4:q G4:q G4:q  A4:q A4:q G4:h  F4:q F4:q E4:q E4:q  D4:q D4:q C4:h' 4 100
