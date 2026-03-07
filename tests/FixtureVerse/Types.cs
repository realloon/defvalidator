namespace Verse;

public class Def
{
    public string? defName;
    public string? label;
}

public class ThingDef : Def
{
    public int statBase;
    public SoundDef? sound;
    public List<ThingDef>? thingRefs;
    public List<CompProperties>? comps;
    public List<DefModExtension>? modExtensions;
}

public class SoundDef : Def
{
}

public class ColorDef : Def
{
    public string? rgb;
}

public class DefModExtension
{
    public string? tag;
}

public class CompProperties
{
    public Type? compClass;
}

public class CompProperties_Glower : CompProperties
{
    public int glowRadius;
}


public class RecipeDef : Def
{
    public List<IngredientCount>? ingredients;
    public List<ThingDef>? recipeUsers;
}

public class IngredientCount
{
    public ThingFilter? filter;
    private float count = 1f;
}

public class ThingFilter
{
    private List<ThingDef>? thingDefs;
}

public class HiddenFieldDef : Def
{
    private int count;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class LoadAliasAttribute : Attribute
{
    public LoadAliasAttribute(string alias)
    {
    }
}

public class AliasedFieldDef : Def
{
    [LoadAlias("priority")]
    private int priorityInt;
}
