namespace BoxPlanLib.Svg;

public interface IRasterVectorizer
{
    // dark[y, x] = true means filled pixel. Returns SVG <path> element string(s).
    string Vectorize(bool[,] dark, int width, int height);
}
