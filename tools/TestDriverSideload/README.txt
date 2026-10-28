Kasa/Tapo Windows driver uploader

Build-TestSideload.ps1 embeds one registered package and its checksum in a single
Windows executable. Use -NormalBuild for a prepared public release; omit it for
a registered customer-test distribution. The executable uploads and verifies
the package over pinned SFTP. It does not update the active driver or reboot.

The customer must then update the existing platform in Home Setup or Configure
Pro. Processor credentials are used for the connection and are not saved.
Use --verify, --self-test, --extract and --preview for offline validation.
loopback_sftp.py exercises the transport against an isolated loopback server.

The temporary diagnostic file logger and log collection have been removed.
Historical published utilities and their frozen payloads remain unchanged.
