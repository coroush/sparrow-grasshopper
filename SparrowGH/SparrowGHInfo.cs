using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace SparrowGH
{
    public class SparrowGHInfo : GH_AssemblyInfo
    {
        public override string Name => "SparrowGH";
        public override Bitmap? Icon => null;
        public override string Description =>
            "Grasshopper bridge for Sparrow — state-of-the-art 2D irregular strip packing (nesting).";
        public override Guid Id => new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        public override string AuthorName => "";
        public override string AuthorContact => "";
        public override string Version => "0.1.0";
    }
}
