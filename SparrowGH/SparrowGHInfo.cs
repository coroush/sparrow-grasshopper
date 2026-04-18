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
        public override Guid Id => new Guid("350e655d-11e3-4ff8-be38-88e1caa8e821");
        public override string AuthorName => "Soroush Garivani";
        public override string AuthorContact => "soroushgarivani@gmail.com";
        public override string Version => "0.2.3";
    }
}
