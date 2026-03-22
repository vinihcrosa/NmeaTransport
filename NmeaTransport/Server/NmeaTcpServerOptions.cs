namespace NmeaTransport.Server;

/// <summary>
/// Defines runtime options for <see cref="NmeaTcpServer"/>.
/// </summary>
public sealed class NmeaTcpServerOptions
{
    /// <summary>
    /// Gets or sets whether the server writes lifecycle and error logs to the terminal.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, the server writes logs to <see cref="Console"/>.
    /// When <see langword="false"/> or <see langword="null"/>, the server does not write terminal logs.
    /// </remarks>
    public bool? EnableLogging { get; set; } = false;
}
