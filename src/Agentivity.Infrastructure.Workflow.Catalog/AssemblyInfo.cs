using System.Runtime.CompilerServices;

// The nodes that talk to a server (a mailbox…) take what they talk through as an internal constructor argument, so that
// their behaviour is tested without the server.
[assembly: InternalsVisibleTo("Agentivity.Unit.Workflow")]
