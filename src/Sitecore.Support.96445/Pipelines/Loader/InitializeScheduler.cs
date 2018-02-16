namespace Sitecore.Support.Pipelines.Loader
{
  using Sitecore.Pipelines;

  public class InitializeScheduler
  {
    public void Process(PipelineArgs args)
    {
      Tasks.Scheduler.Initialize();
    }
  }
}
