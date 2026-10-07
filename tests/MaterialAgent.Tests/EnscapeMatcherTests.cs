using System;
using System.Collections.Generic;
using MaterialAgent.Core;
using Xunit;

namespace MaterialAgent.Tests
{
    public class EnscapeMatcherTests
    {
        static MaterialTypeInfo T(string id, string internalName, string typeName, string plugIn, string file = null) => new MaterialTypeInfo
        {
            Id = Guid.Parse(id), InternalName = internalName, TypeName = typeName, PlugInName = plugIn, PlugInFile = file,
        };

        // Shaped like a real Rhino 8 list: built-in types have readable internal names, third-party ones GUIDs.
        static List<MaterialTypeInfo> RealisticList() => new List<MaterialTypeInfo>
        {
            T("ba51c000-ba51-c000-ba51-c0ba51c00000", "rcm-basic-material", "Custom", "Renderer Development Kit"),
            T("5a8d7b9b-cdc9-49de-8c16-2ef64fb097ab", "5a8d7b9b-cdc9-49de-8c16-2ef64fb097ab", "Physically Based", "Rhino Render"),
            T("8cbed696-cae0-4c62-8714-f11286134cff", "8cbed696-cae0-4c62-8714-f11286134cff", "V-Ray Mtl", "V-Ray for Rhino", @"C:\Program Files\Chaos\V-Ray\VRayForRhino.rhp"),
            T("a6b37849-f705-403a-ac3e-58e083bf3cd6", "a6b37849-f705-403a-ac3e-58e083bf3cd6", "V-Ray Car Paint Mtl", "V-Ray for Rhino"),
            T("a040e9d1-853f-435f-bfb8-5cc4fd88c617", "a040e9d1-853f-435f-bfb8-5cc4fd88c617", "Enscape", "Enscape.Rhino8.Plugin", @"C:\Program Files\Enscape\Bin64\Enscape.Rhino8.Plugin.rhp"),
        };

        [Fact]
        public void FindsEnscapeDespiteGuidInternalName()
        {
            var pick = EnscapeMatcher.Pick(RealisticList());
            Assert.Equal(Guid.Parse("a040e9d1-853f-435f-bfb8-5cc4fd88c617"), pick.Id);
        }

        [Fact]
        public void FindsEnscapeByPlugInFileAlone()
        {
            var list = RealisticList();
            list[4].TypeName = "Material";      // display name doesn't say Enscape
            list[4].PlugInName = null;          // and the plug-in info is missing
            Assert.Equal(list[4].Id, EnscapeMatcher.Pick(list).Id);
        }

        [Fact]
        public void ReturnsNullWhenEnscapeIsNotInstalled()
        {
            var list = RealisticList();
            list.RemoveAt(4);
            Assert.Null(EnscapeMatcher.Pick(list)); // never guesses V-Ray or a built-in type
        }
    }
}
