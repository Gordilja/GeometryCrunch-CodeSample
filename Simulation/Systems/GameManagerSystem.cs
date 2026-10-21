namespace Quantum
{
    using Photon.Deterministic;
    using Quantum.Collections;
    using UnityEngine.Scripting;

    /// Match lifecycle. PvP: lobby countdown → bots fill empty slots → timed match → most kills wins.
    /// Level mode (RuntimeConfig.Level set): short start countdown → LevelSystem runs the waves and ends the game.
    [Preserve]
    public unsafe class GameManagerSystem : SystemMainThread, ISignalOnComponentAdded<GameManager>, ISignalOnPlayerDisconnected,
        ISignalOnPlayerConnected
    {
        public void OnAdded(Frame f, EntityRef entity, GameManager* component)
        {
            var config = f.FindAsset<GameManagerConfig>(component->GameManagerConfig);
            component->TimeToWaitPlayers = config.TimeToWaitForPlayers;
            component->MatchTimeRemaining = config.MatchDuration;

            if (LevelMode.TryGetLevel(f, out var level))
                component->TimeToWaitPlayers = level.StartDelay;
        }

        public void OnPlayerDisconnected(Frame f, PlayerRef player)
        {
            var gameManager = f.Unsafe.GetPointerSingleton<GameManager>();

            foreach (var pair in f.GetComponentIterator<PlayerLink>())
            {
                if (pair.Component.Player == player)
                {
                    // Keep their stats in a return slot: a player who drops and rejoins within Photon's PlayerTTL
                    // (reconnect token) gets a new cube with the same kills / deaths
                    SaveReturnSlot(gameManager, player, pair.Component);
                    f.Destroy(pair.Entity);
                    break;
                }
            }

            // A player who left can't block the rematch vote
            gameManager->RematchVotes &= ~(1L << player);

            EvaluateGameOverCondition(f);
        }

        // Backup for rejoins: Quantum may report the reconnection before re-adding the player
        public void OnPlayerConnected(Frame f, PlayerRef player)
        {
            SpawnSystem.EnsureAvatar(f, player);
        }

        private static void SaveReturnSlot(GameManager* gameManager, PlayerRef player, PlayerLink link)
        {
            if (player < 0 || player >= gameManager->ReturnSlots.Length)
                return;

            *gameManager->ReturnSlots.GetPointer(player) = new SavedPlayerStats
            {
                HasValue = true,
                Kills = link.Kills,
                Deaths = link.Deaths,
                Parries = link.Parries,
                DamageDealt = link.DamageDealt,
                BestStreak = link.BestStreak
            };
        }

        /// Gives a rejoining player back the stats saved when they disconnected (called by SpawnSystem.EnsureAvatar).
        public static void RestoreReturnSlot(Frame f, PlayerRef player, EntityRef avatar)
        {
            var gameManager = f.Unsafe.GetPointerSingleton<GameManager>();
            if (player < 0 || player >= gameManager->ReturnSlots.Length)
                return;

            var slot = gameManager->ReturnSlots.GetPointer(player);
            if (!slot->HasValue)
                return;

            var link = f.Unsafe.GetPointer<PlayerLink>(avatar);
            link->Kills = slot->Kills;
            link->Deaths = slot->Deaths;
            link->Parries = slot->Parries;
            link->DamageDealt = slot->DamageDealt;
            link->BestStreak = slot->BestStreak;
            *slot = default;
        }

        // Dead players keep their entity (respawn), so this only ends the match early when
        // disconnects leave a single player. Otherwise the match ends on the timer.
        private void EvaluateGameOverCondition(Frame f)
        {
            if (LevelMode.IsActive(f))
                return;

            var gameManager = f.Unsafe.GetPointerSingleton<GameManager>();
            if (gameManager->CurrentGameState != GameState.Playing)
                return;

            var count = f.ComponentCount<PlayerLink>();

            if (count > 1)
                return;

            if (GetWinner(f, out var winner))
            {
                f.Events.GameOver(winner);
                gameManager->CurrentGameState = GameState.GameOver;
            }
        }

        public override void Update(Frame frame)
        {
            var gameManager = frame.Unsafe.GetPointerSingleton<GameManager>();
            var isLevel = LevelMode.IsActive(frame);

            switch (gameManager->CurrentGameState)
            {
                case GameState.WaitingForPlayers:
                    gameManager->TimeToWaitPlayers -= frame.DeltaTime;
                    if (gameManager->TimeToWaitPlayers <= FP._0)
                    {
                        var config = frame.FindAsset<GameManagerConfig>(gameManager->GameManagerConfig);
                        if (!isLevel)
                            SpawnBots(frame, config);

                        gameManager->CurrentGameState = GameState.Playing;
                        frame.Events.GameplayStarted();
                        gameManager->MatchTimeRemaining = config.MatchDuration;
                    }
                    break;

                case GameState.GameOver:
                    if (!isLevel)
                        UpdateRematchVotes(frame, gameManager);
                    break;

                case GameState.Playing:
                    // Levels have no match timer: LevelSystem ends the game when the last wave is cleared
                    if (isLevel)
                        break;

                    gameManager->MatchTimeRemaining -= frame.DeltaTime;
                    if (gameManager->MatchTimeRemaining <= FP._0)
                    {
                        gameManager->CurrentGameState = GameState.GameOver;
                        if (GetWinner(frame, out var winner))
                            frame.Events.GameOver(winner);
                    }
                    break;
            }
        }

        // "Play again": collect votes (CommandPlayAgain). The rematch starts when every connected human has voted;
        // players who leave drop out of the count (OnPlayerDisconnected clears their vote).
        private static void UpdateRematchVotes(Frame f, GameManager* gameManager)
        {
            for (int p = 0; p < f.MaxPlayerCount; p++)
            {
                if (f.GetPlayerCommand(p) is CommandPlayAgain)
                    gameManager->RematchVotes |= 1L << p;
            }

            GetRematchCounts(f, out var ready, out var needed);
            if (needed > 0 && ready == needed)
                StartRematch(f, gameManager);
        }

        public static bool IsConnected(Frame f, PlayerRef player)
        {
            return (f.GetPlayerInputFlags(player) & DeterministicInputFlags.PlayerNotPresent) == 0;
        }

        /// Connected humans and how many of them voted to play again (also shown on the game-over screen).
        public static void GetRematchCounts(Frame f, out int ready, out int needed)
        {
            ready = needed = 0;
            var votes = f.GetSingleton<GameManager>().RematchVotes;
            foreach (var pair in f.GetComponentIterator<PlayerLink>())
            {
                var player = pair.Component.Player;
                if (!player.IsValid || !IsConnected(f, player))
                    continue;

                needed++;
                if ((votes & (1L << player)) != 0)
                    ready++;
            }
        }

        // Same room, fresh match: clear the arena, reset every human, go back to the lobby countdown.
        // Bots are removed and refilled by SpawnBots when the countdown ends (someone may join meanwhile).
        private static void StartRematch(Frame f, GameManager* gameManager)
        {
            var config = f.FindAsset<GameManagerConfig>(gameManager->GameManagerConfig);

            var toDestroy = f.AllocateList<EntityRef>();
            foreach (var pair in f.GetComponentIterator<Bot>()) toDestroy.Add(pair.Entity);
            foreach (var pair in f.GetComponentIterator<PickupItem>()) toDestroy.Add(pair.Entity);
            foreach (var pair in f.GetComponentIterator<Bullet>()) toDestroy.Add(pair.Entity);
            foreach (var pair in f.GetComponentIterator<DestroyAfter>()) toDestroy.Add(pair.Entity);
            for (int i = 0; i < toDestroy.Count; i++)
            {
                if (f.Exists(toDestroy[i]))
                    f.Destroy(toDestroy[i]);
            }
            f.FreeList((QListPtr<EntityRef>)toDestroy);

            // Fresh spawn rotation
            var spawnPointManager = f.Unsafe.GetPointerSingleton<SpawnPointManager>();
            f.ResolveList(spawnPointManager->AvailableSpawnPoints).Clear();
            f.ResolveList(spawnPointManager->UsedSpawnPoints).Clear();

            foreach (var pair in f.Unsafe.GetComponentBlockIterator<PlayerLink>())
                ResetPlayer(f, pair.Entity, pair.Component);

            for (int i = 0; i < gameManager->ReturnSlots.Length; i++)
                *gameManager->ReturnSlots.GetPointer(i) = default;

            ShrinkingCircleSystem.ResetToStart(f);

            gameManager->RematchVotes = 0;
            gameManager->CurrentGameState = GameState.WaitingForPlayers;
            gameManager->TimeToWaitPlayers = config.RematchLobbyTime;
            gameManager->MatchTimeRemaining = config.MatchDuration;
            gameManager->NextPickupSpawnTime = FP._0;

            f.Events.RematchStarted();
        }

        private static void ResetPlayer(Frame f, EntityRef entity, PlayerLink* link)
        {
            var wasDead = link->IsDead;
            var deathPosition = link->DeathPosition;

            link->Kills = 0;
            link->Deaths = 0;
            link->Parries = 0;
            link->DamageDealt = FP._0;
            link->Streak = 0;
            link->BestStreak = 0;
            link->IsDead = false;
            link->RespawnTimer = FP._0;

            SpawnSystem.PlacePlayerOnSpawnPoint(f, entity);
            var transform = f.Unsafe.GetPointer<Transform2D>(entity);
            transform->Teleport(f, transform->Position);

            if (f.Unsafe.TryGetPointer<TopDownKCC>(entity, out var kcc))
            {
                kcc->Velocity = FPVector2.Zero;
                kcc->Force = FPVector2.Zero;
            }

            if (f.Unsafe.TryGetPointer<Parry>(entity, out var parry))
            {
                parry->IsParrying = false;
                parry->WindowTimer = FP._0;
                parry->CooldownTimer = FP._0;
            }

            if (f.Unsafe.TryGetPointer<Damageable>(entity, out var damageable))
            {
                var data = f.FindAsset<DamageableBase>(damageable->DamageableData);
                damageable->Health = data.MaxHealth;
                f.Events.DamageableHealthUpdate(entity, data.MaxHealth, data.MaxHealth);

                // Back to the starting weapon
                if (data is PlayerDamageable playerData && playerData.Weapons != null && playerData.Weapons.Length > 0
                    && f.Unsafe.TryGetPointer<Weapon>(entity, out var weapon))
                {
                    if (weapon->IsShooting)
                    {
                        weapon->IsShooting = false;
                        f.Events.PlayerStoppedShooting(entity);
                    }

                    var start = playerData.Weapons[0];
                    if (weapon->WeaponData.Id != start.Guid)
                    {
                        weapon->WeaponData = start;
                        f.Events.WeaponChanged(entity, WeaponType.SingleShot);
                    }
                    weapon->CooldownTime = FP._0;
                }
            }

            // Dead players get their cube back through the normal respawn path in the view
            if (wasDead)
                f.Events.PlayerRespawned(entity, deathPosition, transform->Position);
        }

        // Runs once, on the tick the lobby countdown ends (PvP only). Bots are regular avatars with
        // PlayerLink.Player = None and a Bot component; see BotSpawner.
        private static void SpawnBots(Frame f, GameManagerConfig config)
        {
            var botCount = config.MatchPlayerCount - f.ComponentCount<PlayerLink>();
            if (botCount <= 0)
                return;

            var avatar = config.BotAvatar.IsValid ? config.BotAvatar : SpawnSystem.FindHumanAvatar(f);
            if (!avatar.IsValid)
            {
                Log.Warn("No bot avatar: set GameManagerConfig.BotAvatar (no human avatar to copy either) — no bots spawned.");
                return;
            }

            var difficulties = config.BotDifficulties;
            if (difficulties == null || difficulties.Length == 0)
            {
                Log.Warn("GameManagerConfig.BotDifficulties is empty — no bots spawned.");
                return;
            }

            for (int i = 0; i < botCount; i++)
            {
                var difficulty = PickDifficulty(difficulties, i, botCount);
                if (difficulty == null)
                {
                    Log.Warn("GameManagerConfig.BotDifficulties has an empty slot — bot skipped.");
                    continue;
                }

                var entity = BotSpawner.Spawn(f, avatar, i + 1, difficulty, null);
                SpawnSystem.PlacePlayerOnSpawnPoint(f, entity);
            }
        }

        // Spreads botCount bots evenly over the profiles (ordered easiest → hardest): with 3 profiles,
        // 1 bot = middle, 2 = first + last, 3 = one each, 5 = 2 easy, 2 normal, 1 hard.
        private static BotDifficulty PickDifficulty(BotDifficulty[] difficulties, int botIndex, int botCount)
        {
            var last = difficulties.Length - 1;
            var index = botCount == 1 ? last / 2 : botIndex * last / (botCount - 1);
            return difficulties[index];
        }

        private bool GetWinner(Frame f, out EntityRef entityRef)
        {
            entityRef = EntityRef.None;
            int bestKills = -1;
            FP bestHP = FP.MinValue;

            foreach (var pair in f.GetComponentIterator<PlayerLink>())
            {
                int kills = pair.Component.Kills;
                FP hp = FP._0;

                if (f.TryGet<Damageable>(pair.Entity, out var damageable))
                    hp = damageable.Health;

                if (kills > bestKills || (kills == bestKills && hp > bestHP))
                {
                    bestKills = kills;
                    bestHP = hp;
                    entityRef = pair.Entity;
                }
            }

            return entityRef != EntityRef.None;
        }
    }
}
