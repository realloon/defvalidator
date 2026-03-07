using Verse;

namespace TestModTypes;

public class CustomThingDef : ThingDef
{
    public CustomPayload? customPayload;
    public ColorDef? accentColor;
}

public class CustomPayload
{
    public int number;
}

public class CustomCompProperties : CompProperties
{
    public string? customText;
}
