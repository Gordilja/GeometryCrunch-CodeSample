namespace Quantum
{
    using Photon.Deterministic;
    using UnityEngine;

    public unsafe class PlayerDamageable : DamageableBase
    {
        public FP RespawnTime = FP._3;

        [Tooltip("Order matters: [0]=Single, [1]=Dual, [2]=Triple, [3]=Quad")]
        public WeaponData[] Weapons;

        public override unsafe void DamageableHit(Frame f, EntityRef victim, EntityRef hitter, FP damage, Damageable* damageable)
        {
            var playerLink = f.Unsafe.GetPointer<PlayerLink>(victim);

            // Already dead — ignore extra hits in the same tick (e.g. quad shot) so death logic runs once
            if (playerLink->IsDead)
                return;

            // Match stats: damage done to someone else (shooting costs HP with hitter == victim; the zone has no hitter)
            PlayerLink* hitterLink = null;
            bool otherHitter = hitter != victim && hitter != EntityRef.None && f.Unsafe.TryGetPointer<PlayerLink>(hitter, out hitterLink);
            if (otherHitter)
                hitterLink->DamageDealt += FPMath.Min(damage, FPMath.Max(damageable->Health, FP._0));

            damageable->Health -= damage;

            // Level enemies lock onto a player who shoots them, even from outside their detection range
            BotBehaviours.OnDamagedBy(f, victim, hitter);

            if (damageable->Health <= 0)
            {
                // Credit kill (not self-kill) and track streaks
                if (otherHitter)
                {
                    hitterLink->Kills++;
                    hitterLink->Streak++;
                    if (hitterLink->Streak > hitterLink->BestStreak)
                        hitterLink->BestStreak = hitterLink->Streak;
                }

                playerLink->Deaths++;
                playerLink->Streak = 0;

                // Mark dead instead of destroying
                var transform = f.Get<Transform2D>(victim);
                playerLink->IsDead = true;
                playerLink->RespawnTimer = RespawnTime;
                playerLink->DeathPosition = transform.Position;

                // Pick a random spawn point for respawn
                playerLink->RespawnPosition = GetRandomSpawnPosition(f);

                // Stop residual movement so the dead body doesn't drift and respawn keeps no momentum
                if (f.Unsafe.TryGetPointer<TopDownKCC>(victim, out var kcc))
                {
                    kcc->Velocity = FPVector2.Zero;
                    kcc->Force = FPVector2.Zero;
                }

                // Close any open parry window
                if (f.Unsafe.TryGetPointer<Parry>(victim, out var parry))
                {
                    parry->IsParrying = false;
                    parry->WindowTimer = FP._0;
                }

                // Downgrade weapon on death
                if (f.Unsafe.TryGetPointer<Weapon>(victim, out var weapon))
                {
                    // WeaponSystem skips dead players, so close the shooting state here
                    if (weapon->IsShooting)
                    {
                        weapon->IsShooting = false;
                        f.Events.PlayerStoppedShooting(victim);
                    }

                    var currentData = f.FindAsset<WeaponData>(weapon->WeaponData);
                    if (currentData.WeaponType != WeaponType.SingleShot)
                    {
                        WeaponType downgraded = WeaponHelper.GetPreviousTier(currentData.WeaponType);
                        int index = WeaponHelper.WeaponTypeToIndex(downgraded);
                        var newData = Weapons[index];
                        weapon->WeaponData = newData;
                        weapon->CooldownTime = newData.Cooldown;
                        f.Events.WeaponChanged(victim, downgraded);
                    }
                }

                f.Events.PlayerKilled(hitter, victim);
                LevelMode.OnParticipantKilled(f, victim);

                var lootDrops = 4;
                if (f.TryGet<LevelEnemy>(victim, out var levelEnemy))
                {
                    // Wave enemies don't respawn (RespawnSystem skips them): remove the body after the death visual
                    var archetype = f.FindAsset(levelEnemy.Archetype);
                    lootDrops = archetype != null ? archetype.LootDrops : 1;
                    f.Add(victim, new DestroyAfter { Type = DestroyObjectType.DeathVisual });
                }

                DropLoot(f, victim, lootDrops);
                return;
            }

            f.Events.DamageableHealthUpdate(victim, MaxHealth, damageable->Health);
        }

        private FPVector2 GetRandomSpawnPosition(Frame f)
        {
            // Pick uniformly from all spawn points. Doesn't touch SpawnPointManager's join rotation lists.
            var count = f.ComponentCount<SpawnPoint>();
            if (count == 0)
                return FPVector2.Zero;

            var targetIndex = f.RNG->Next(0, count);
            var index = 0;

            foreach (var pair in f.GetComponentIterator<SpawnPoint>())
            {
                if (index++ == targetIndex)
                    return f.Get<Transform2D>(pair.Entity).Position;
            }

            return FPVector2.Zero;
        }

        private void DropLoot(Frame f, EntityRef victim, int count)
        {
            var transform = f.Get<Transform2D>(victim);

            var deathVisual = f.Create(f.SimulationConfig.DeathVisual);
            f.Unsafe.GetPointer<Transform2D>(deathVisual)->Position = transform.Position;

            // Lifetime comes from DestroyAfterSystem based on Type
            f.Add(deathVisual, new DestroyAfter { Type = DestroyObjectType.DeathVisual });

            for (int i = 0; i < count; i++)
            {
                // Integer RNG converts to FP implicitly — never use FromFloat_UNSAFE at runtime (desyncs)
                FP angle = (FP._360 / count) * i + f.RNG->Next(-15, 15);
                FPVector2 dir = FPVector2.Rotate(FPVector2.Up, angle * FP.Deg2Rad);

                var pickupEntity = f.Create(f.SimulationConfig.HealthPickupItem);
                var pickupTransform = f.Unsafe.GetPointer<Transform2D>(pickupEntity);
                pickupTransform->Position = transform.Position + dir * 3;

                var pickupItem = f.Unsafe.GetPointer<PickupItem>(pickupEntity);
                pickupItem->HasSpawnArc = true;
                pickupItem->SpawnOrigin = transform.Position;
            }
        }
    }
}
