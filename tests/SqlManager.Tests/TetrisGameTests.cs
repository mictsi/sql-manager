namespace SqlManager.Tests;

public sealed class TetrisGameTests
{
    [Fact]
    public void Tick_MovesActivePieceDownWhenSpaceIsAvailable()
    {
        var game = new TetrisGame(random: new Random(0));
        var before = game.ActiveCells.Select(cell => cell.Y).ToArray();

        game.Tick();

        var after = game.ActiveCells.Select(cell => cell.Y).ToArray();
        Assert.Equal(before.Select(y => y + 1), after);
    }

    [Fact]
    public void MoveLeft_DoesNotCrossLeftWall()
    {
        var game = new TetrisGame(random: new Random(0));

        for (var step = 0; step < 10; step++)
        {
            game.MoveLeft();
        }

        Assert.True(game.ActiveCells.Min(cell => cell.X) >= 0);
    }

    [Fact]
    public void HardDrop_ClearsCompletedLineAndAddsScore()
    {
        var game = new TetrisGame(width: 10, height: 12, random: new Random(0));
        for (var x = 0; x < 8; x++)
        {
            game.SetCell(x, 11, true);
        }

        game.SetActivePieceForTest(TetrisPieceType.O, 0, 7, 0);
        game.HardDrop();

        Assert.Equal(1, game.LinesCleared);
        Assert.Equal(100, game.Score);
    }
}