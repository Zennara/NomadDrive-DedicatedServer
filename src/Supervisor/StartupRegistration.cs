using System.Diagnostics;

namespace NomadDrive.Server;

public static class StartupRegistration
{
    // Only call immediately after creating a process, before it is handed to the
    // ready-session lifecycle. This never receives an already established server.
    public static T Complete<T>(Process child, Func<T> register, TimeSpan exitTimeout)
    {
        var pid = child.Id;
        try { return register(); }
        catch (Exception registrationFailure)
        {
            try
            {
                if (!child.HasExited)
                {
                    try { child.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (child.HasExited) { }
                }
                if (!child.WaitForExit(exitTimeout))
                    throw new TimeoutException("The new process did not exit within the startup-abort timeout.");
            }
            catch (Exception abortFailure)
            {
                throw new IOException($"Startup registration failed for PID {pid}, and its termination could not be confirmed. " +
                    "Do not start another instance until this process has been checked.",
                    new AggregateException(registrationFailure, abortFailure));
            }
            throw new IOException($"Startup registration failed for PID {pid}. The just-launched process was stopped before a ready session was established. " +
                registrationFailure.Message, registrationFailure);
        }
    }
}
