$ErrorActionPreference = 'Stop'
$eventNames = @(
    'TurnWindowOpenedEvent', 'UnitDiedEvent', 'UnitStateChangedEvent',
    'DamageChannelResolvedEvent', 'WallImpactDamageEvent',
    'ReactionPlanResolvedEvent', 'AdrenalineLedgerChangedEvent'
)
$output = @(
    foreach ($q in 4,6) {
        $path = Join-Path $PSScriptRoot "replays/ai-Q$q-wall-A20M30-R20-Blunt-Blunt-SustainedOffense-mirrorFalse.heroReplay"
        $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($path), [Text.Encoding]::UTF8)
        try {
            if ($reader.ReadString() -ne 'ProjectHero.DeveloperReplay.v6' -or $reader.ReadInt32() -ne 7) {
                throw 'Unexpected replay format'
            }
            $null = $reader.ReadString() # rules version
            $hash = $reader.ReadString()
            $null = $reader.ReadInt32()  # Tick rate
            $null = $reader.ReadString() # encounter ID
            $null = $reader.ReadUInt64() # RNG seed
            $null = $reader.ReadInt32()  # initial meta resource
            $null = $reader.ReadUInt64() # initial state hash
            if ($reader.ReadInt32() -ne 0) { throw 'Expected AI-only replay with no player submissions' }
            $records = $reader.ReadInt32()
            for ($i = 0; $i -lt $records; $i++) {
                $tick = $reader.ReadInt64()
                $null = $reader.ReadUInt64() # snapshot hash
                $count = $reader.ReadInt32()
                for ($j = 0; $j -lt $count; $j++) {
                    if ($reader.ReadInt64() -ne $tick) { throw 'Event Tick does not match the record' }
                    $sequence = $reader.ReadInt64()
                    $fact = $reader.ReadString()
                    if ($tick -gt 180 -or $fact -notmatch '^ProjectHero\.Logic\.Events\.([^{]+)\{' -or $Matches[1] -notin $eventNames) { continue }
                    [ordered]@{ Q = $q; Hash = $hash; Tick = $tick; Seq = $sequence; Event = $Matches[1]; Fact = $fact } |
                        ConvertTo-Json -Compress
                }
            }
            if ($reader.BaseStream.Position -ne $reader.BaseStream.Length) { throw 'Unread replay data' }
        } finally {
            $reader.Dispose()
        }
    }
)
$output | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'comparison-trace.jsonl') -Encoding utf8
