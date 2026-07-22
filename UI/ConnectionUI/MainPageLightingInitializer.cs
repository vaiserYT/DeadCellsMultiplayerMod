using dc.haxe.ds;
using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection.LightingInitializer
{
    public class MainPageLightingInitializer
    {

        public MainPageLightingInitializer(ConnectionUI connection)
        {
            InitializeLightingForMainPage(connection);
        }

        public dc.h3d.Vector? ligttshadow;
        public dc.h3d.Vector? lightDirVecto;

        private void InitializeLightingForMainPage(ConnectionUI uI)
        {

            double x = 1.0;
            double y = 0;
            double z = 0;
            double w = 1.0;
            this.ligttshadow = new dc.h3d.Vector(Ref<double>.From(ref y), Ref<double>.From(ref z), Ref<double>.From(ref w), Ref<double>.From(ref x));


            x = (double)-1;
            y = (double)0;
            z = (double)-1;
            this.lightDirVecto = new dc.h3d.Vector(Ref<double>.From(ref x), Ref<double>.From(ref y), Ref<double>.From(ref z), Ref<double>.Null);
            this.lightDirVecto.normalize();

            dc.h2d.RenderContext ctx = uI.root.getScene().ctx;
            IntMap map = ctx.manager.globals.map;
            map.set(2, ligttshadow);
            map.set(3, lightDirVecto);
        }


    }
}