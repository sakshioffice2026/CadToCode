namespace CadToCode.Business.Interfaces;

public interface IImageTextRecognizer
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<RecognizedWord>> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken = default);
}

public sealed record RecognizedWord(string Text, double Confidence, int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;
}