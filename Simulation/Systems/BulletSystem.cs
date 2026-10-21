namespace Quantum
{
    using Photon.Deterministic;
    using UnityEngine.Scripting;

    [Preserve]
    public unsafe class BulletSystem : SystemMainThreadFilter<BulletSystem.Filter>, ISignalCreateBullet
    {
        public void CreateBullet(Frame f, EntityRef owner, WeaponData weaponData)
        {
            var bulletData = weaponData.BulletData;
            bulletData.CreateBullet(f, weaponData, owner);
        }

        public override void Update(Frame frame, ref Filter filter)
        {
            var nextPos = filter.Bullet->Direction * filter.Bullet->Speed * frame.DeltaTime;

            if (CheckForCollision(frame, filter, nextPos, out var entityHit))
            {
                // Check if the target is parrying
                if (frame.Unsafe.TryGetPointer<Parry>(entityHit, out var parry) && parry->IsParrying)
                {
                    // Absorb as HP
                    if (frame.Unsafe.TryGetPointer<Damageable>(entityHit, out var damageable))
                    {
                        var healAmount = filter.Bullet->Damage * parry->AbsorbMultiplier;
                        frame.Signals.DamageableHealthRestore(entityHit, healAmount, damageable);
                        frame.Events.ParrySuccess(entityHit, healAmount);
                    }

                    if (frame.Unsafe.TryGetPointer<PlayerLink>(entityHit, out var parryLink))
                        parryLink->Parries++;

                    frame.Destroy(filter.Entity);
                    return;
                }

                if (frame.Unsafe.TryGetPointer<Damageable>(entityHit, out var dmg))
                {
                    frame.Signals.DamageableHit(entityHit, filter.Bullet->Owner, filter.Bullet->Damage, dmg);
                }

                frame.Events.BulletHitEffect(filter.Transform->Position + nextPos);
                frame.Destroy(filter.Entity);
                return;
            }

            if (TickLifetime(frame, filter))
            {
                frame.Destroy(filter.Entity);
                return;
            }

            filter.Transform->Position += nextPos;
        }

        /// Returns true when the bullet's lifetime has run out.
        private static bool TickLifetime(Frame frame, Filter filter)
        {
            filter.Bullet->Time -= frame.DeltaTime;
            return filter.Bullet->Time <= FP._0;
        }

        // Bullets are swept as a small circle instead of an infinitely thin line, so shots that visually clip
        // a cube actually hit (a 0.5-radius player becomes a ~1.7-wide target instead of 1.0)
        static readonly FP BulletHitRadius = FP._0_33;

        private static bool CheckForCollision(Frame frame, Filter filter, FPVector2 nextPos, out EntityRef entityHit)
        {
            entityHit = EntityRef.None;
            var owner = filter.Bullet->Owner;
            var position = filter.Transform->Position;
            var collisions = frame.Physics2D.ShapeCastAll(position, FP._0, Shape2D.CreateCircle(BulletHitRadius), nextPos,
                int.MaxValue, QueryOptions.HitAll & ~QueryOptions.HitTriggers);

            // Nearest hit along the path first (wall in front of a player blocks the shot)
            collisions.SortCastDistance();

            for (int i = 0; i < collisions.Count; i++)
            {
                var collision = collisions[i];
                if (collision.Entity == filter.Entity || collision.Entity == owner)
                    continue;

                // Dead players keep their collider (entity is preserved for respawn) — let bullets pass through
                if (frame.TryGet<PlayerLink>(collision.Entity, out var playerLink) && playerLink.IsDead)
                    continue;

                // No friendly fire between allies (level wave enemies); PvP has no allies
                if (collision.Entity != EntityRef.None && Teams.AreAllies(frame, owner, collision.Entity))
                    continue;

                entityHit = collision.Entity;
                return true;
            }

            return false;
        }

        public struct Filter
        {
            public EntityRef Entity;
            public Bullet* Bullet;
            public Transform2D* Transform;
        }
    }
}
