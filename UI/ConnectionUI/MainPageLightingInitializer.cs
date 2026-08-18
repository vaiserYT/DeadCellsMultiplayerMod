using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection.LightingInitializer
{
    /// <summary>
    /// Title-screen DirLighted globals (slots 2/3). ColorMap+DirLighted will not remap
    /// lobby beheaded skins unless these stay set on the scene that actually draws them.
    /// </summary>
    public class MainPageLightingInitializer
    {
        public MainPageLightingInitializer(ConnectionUI connection)
        {
            EnsureVectors();
            Apply(connection);
        }

        public dc.h3d.Vector? ligttshadow;
        public dc.h3d.Vector? lightDirVecto;

        public void Apply(ConnectionUI? ui)
        {
            if (ui?.root == null)
                return;

            EnsureVectors();

            try
            {
                var scene = ui.root.getScene();
                var ctx = scene?.ctx;
                var map = ctx?.manager?.globals?.map;
                if (map == null)
                    return;

                map.set(2, ligttshadow);
                map.set(3, lightDirVecto);
            }
            catch
            {
            }
        }

        private void EnsureVectors()
        {
            if (this.ligttshadow == null)
            {
                double x = 1.0;
                double y = 1.0;
                double z = 1.0;
                double w = 1.0;
                this.ligttshadow = new dc.h3d.Vector(
                    Ref<double>.From(ref x),
                    Ref<double>.From(ref y),
                    Ref<double>.From(ref z),
                    Ref<double>.From(ref w));
            }

            if (this.lightDirVecto == null)
            {
                double x = 0;
                double y = 0;
                double z = 1.0;
                double w = 1.0;
                this.lightDirVecto = new dc.h3d.Vector(
                    Ref<double>.From(ref x),
                    Ref<double>.From(ref y),
                    Ref<double>.From(ref z),
                    Ref<double>.From(ref w));
            }
        }
    }
}
