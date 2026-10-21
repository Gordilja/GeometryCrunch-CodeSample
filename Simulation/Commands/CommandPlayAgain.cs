namespace Quantum
{
    using Photon.Deterministic;

    /// Sent from the game-over screen: this player votes for a rematch in the same room.
    /// GameManagerSystem starts the rematch once every connected human has voted.
    public class CommandPlayAgain : DeterministicCommand
    {
        public override void Serialize(BitStream stream)
        {
            // No payload: the vote is the command itself
        }
    }
}
