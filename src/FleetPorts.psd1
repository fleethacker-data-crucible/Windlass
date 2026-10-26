# Windlass's line from the edge-ops port registry (PORTS.md: the "New tool B" block 8777-8779).
# exclusive: Windlass holds a long-running model load and generation, so a running copy is never stopped automatically.
@{
    'windlass' = @{ Start = 8777; Size = 3; Policy = 'exclusive' }
}
