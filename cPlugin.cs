using CSiAPIv1;

namespace BeamSchedulePlugin;

public class cPlugin : cPluginContract
{
    public int Info(ref string text)
    {
        text = "Beam Schedule Plugin: exports a draft schedule of ETABS concrete beam design demands. "
            + "Reinforcement selection and detailing are not implemented.";
        return 0;
    }

    public void Main(ref cSapModel sapModel, ref cPluginCallback pluginCallback)
    {
        int errorCode = 0;
        try
        {
            using var form = new BeamScheduleForm(sapModel);
            form.ShowDialog();
        }
        catch (Exception ex)
        {
            errorCode = 1;
            MessageBox.Show(
                $"The plugin could not start:{Environment.NewLine}{ex}",
                "Beam Schedule Plugin",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            pluginCallback.Finish(errorCode);
        }
    }
}
