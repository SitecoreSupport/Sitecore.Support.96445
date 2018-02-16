using System;
using System.Collections;
using System.Globalization;
using System.Threading;
using System.Web;
using System.Web.Hosting;
using System.Xml;
using Sitecore.Configuration;
using Sitecore.Diagnostics;
using Sitecore.Jobs;
using Sitecore.Security.Accounts;
using Sitecore.SecurityModel;
using Sitecore.Xml;

namespace Sitecore.Support.Tasks
{
  /// <summary>
  /// Represents the Scheduler.
  /// </summary>
  public class Scheduler
  {

    #region Fields

    static TimeSpan m_interval;
    static Agent[] m_agents;

    static DateTime m_startTime;

    static object m_lock = new object();

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes the <see cref="Scheduler"/> class.
    /// </summary>
    static Scheduler()
    {
      Log.Info("Scheduler - Initializing", typeof(Scheduler));

      m_startTime = DateTime.UtcNow;

      var frequency = Factory.GetString("scheduling/frequency", false);

      m_interval = DateUtil.ParseTimeSpan(frequency, TimeSpan.FromMinutes(1), CultureInfo.InvariantCulture);

      if (m_interval.TotalSeconds > 0)
      {
        Log.Info("Scheduler - Interval set to: " + m_interval, typeof(Scheduler));

        var thread = new Thread(WorkLoop);

        thread.Start();

        Diagnostics.PerformanceCounters.SystemCount.ThreadingBackgroundThreadsStarted.Increment();

        Log.Info("Scheduler - Worker thread started", typeof(Scheduler));
      }
      else
      {
        Log.Info("Scheduler - Scheduling is disabled (interval is 00:00:00)", typeof(Scheduler));
      }
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the agents.
    /// </summary>
    /// <value>The agents.</value>
    static Agent[] Agents
    {
      get
      {
        if (m_agents == null)
        {
          lock (m_lock)
          {
            if (m_agents == null)
            {
              m_agents = ReadAgents();
            }
          }
        }

        if (m_agents != null)
        {
          return m_agents;
        }

        return new Agent[0];
      }
    }

    #endregion

    #region Public methods

    /// <summary>
    /// Initializes this instance.
    /// </summary>
    public static void Initialize()
    {
      // dummy method to force constructor to run
    }

    /// <summary>
    /// Runs the processor.
    /// </summary>
    public static void Process()
    {
      lock (m_lock)
      {
        Agent[] agents = Agents;

        foreach (var agent in agents)
        {
          try
          {
            if (agent.IsDue)
            {
              agent.Execute();
            }
          }
          catch (Exception ex)
          {
            Log.Error("Exception in schedule agent: " + agent.Name, ex, typeof(Scheduler));
          }
        }
      }
    }

    #endregion

    #region Worker loop

    /// <summary>
    /// Main scheduler loop
    /// </summary>
    static void WorkLoop()
    {
      while (true)
      {
        Thread.Sleep(m_interval);

        if (HostingEnvironment.ShutdownReason != ApplicationShutdownReason.None)
        {
          continue;
        }

        try
        {
          Process();
        }
        catch
        {
          // silent
        }
      }
    }

    #endregion

    #region Private methods

    /// <summary>
    /// Reads the agents.
    /// </summary>
    /// <returns></returns>
    private static Agent[] ReadAgents()
    {
      Log.Info("Scheduler - Adding agents", typeof(Scheduler));
      try
      {
        var list = new ArrayList();
        foreach (XmlNode node in Factory.GetConfigNodes("scheduling/agent"))
        {
          try
          {
            var obj2 = Factory.CreateObject(node, true);
            string[] values = { XmlUtil.GetAttribute("method", node), "Execute" };
            string method = StringUtil.GetString(values);
            var interval = DateUtil.ParseTimeSpan(XmlUtil.GetAttribute("interval", node), TimeSpan.FromSeconds(0.0), CultureInfo.InvariantCulture);
            string[] textArray2 = { XmlUtil.GetAttribute("name", node), obj2.GetType().FullName };
            var name = StringUtil.GetString(textArray2);
            var @bool = MainUtil.GetBool(XmlUtil.GetAttribute("async", node), false);
            if (interval.TotalSeconds > 0.0)
            {
              Log.Info(string.Concat("Scheduler - Adding agent: ", name, " (interval: ", interval, ")"), typeof(Scheduler));
              Agent agent;
              agent = @bool ? new AsyncAgent(name, obj2, method, interval, m_startTime) : new Agent(name, obj2, method, interval, m_startTime);
              list.Add(agent);
            }
            else
            {
              Log.Info("Scheduler - Skipping inactive agent: " + name, typeof(Scheduler));
            }
          }
          catch (Exception exception)
          {
            Log.Error("Error while instantiating agent. Definition: " + node.OuterXml, exception, typeof(Scheduler));
          }
        }
        Log.Info("Scheduler - Agents added", typeof(Scheduler));
        return (list.ToArray(typeof(Agent)) as Agent[]);
      }
      catch (Exception exception2)
      {
        Log.Error("Error while reading agents.", exception2, typeof(Scheduler));
      }
      return null;
    }



    #endregion

    #region Embedded class - Agent

    /// <summary>
    /// Represents an Agent.
    /// </summary>

    protected internal class Agent
    {
      private readonly string jobMethod;
      private readonly object jobObject;

      /// <inheritdoc />
      public Agent(string name, object obj, string method, TimeSpan interval, DateTime lastRun)
      {
        Error.AssertString(name, "name", false);
        Error.AssertObject(obj, "obj");
        Error.AssertString(method, "method", false);
        this.Name = name;
        this.jobObject = obj;
        this.jobMethod = method;
        this.Interval = interval;
        this.LastRun = DateUtil.ToUniversalTime(lastRun);
      }

      /// <summary>
      /// 
      /// </summary>
      /// <param name="options"></param>
      /// <returns></returns>
      protected virtual Job CreateJob(JobOptions options)
      {
        Assert.IsNotNull(options, "options");
        return new Job(options);
      }

      /// <summary>
      /// 
      /// </summary>
      /// <returns></returns>
      protected virtual JobOptions CreateJobOptions() =>
          new JobOptions(this.Name, "schedule", "scheduler", this.jobObject, this.jobMethod)
          {
            SiteName = "scheduler",
            ContextUser = this.GetAgentContextUser()
          };

      /// <summary>
      /// 
      /// </summary>
      /// <returns></returns>
      public virtual void Execute()
      {
        try
        {
          var options = this.CreateJobOptions();
          var job = this.CreateJob(options);
          this.StartJob(job);
          job.WaitHandle.WaitOne();
        }
        finally
        {
          this.LastRun = DateTime.UtcNow;
        }
      }

      /// <summary>
      /// wwww
      /// </summary>
      protected virtual User GetAgentContextUser() => (DomainManager.GetDomain("sitecore") ?? DomainManager.GetDefaultDomain()).GetAnonymousUser();

      /// <summary>
      /// 
      /// </summary>
      /// <param name="job"></param>
      protected virtual void StartJob(Job job)
      {
        Assert.IsNotNull(job, "job");
        JobManager.Start(job);
      }

      /// <summary>
      /// www
      /// </summary>
      protected TimeSpan Interval { get; }

      /// <summary>
      /// www
      /// </summary>
      public virtual bool IsDue =>
          ((DateTime.UtcNow - this.LastRun) > this.Interval);

      /// <summary>
      /// www
      /// </summary>
      protected DateTime LastRun { get; set; }

      /// <summary>
      /// 
      /// </summary>
      /// <returns></returns>
      public string Name { get; }
    }


    /// <summary>
    /// Represents an AsyncAgent.
    /// </summary>
    protected internal class AsyncAgent : Agent
    {
      private Job agentJob;

      /// <inheritdoc />
      public AsyncAgent(string name, object obj, string method, TimeSpan interval, DateTime lastRun) : base(name, obj, method, interval, lastRun)
      {
      }

      /// <inheritdoc />
      public override void Execute()
      {
        try
        {
          var options = this.CreateJobOptions();
          var job = this.CreateJob(options);
          job.Finished += (sender, args) =>
          {
            var dateTime = (base.LastRun = DateTime.UtcNow);
          };
          this.StartJob(job);
          this.agentJob = job;
        }
        finally
        {
          base.LastRun = DateTime.UtcNow;
        }
      }

      /// <summary>
      /// 
      /// </summary>
      public override bool IsDue
      {
        get
        {
          if (this.agentJob != null)
          {
            if (!this.agentJob.IsDone)
            {
              return false;
            }
            this.agentJob = null;
          }
          return base.IsDue;
        }
      }
    }


    #endregion
  }
}
