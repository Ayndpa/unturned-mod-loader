namespace UnturnedModLoader.ViewModels;

public class CategoryViewModel(string name, int count)
{
    public string Name { get; } = name;
    public int Count { get; } = count;
    public string Label => $"{Name} ({Count})";
}