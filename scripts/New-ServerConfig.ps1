# Run locally with PowerShell 7. The output contains private credentials; never commit it.
[CmdletBinding()]
param([Parameter(Mandatory)][uri]$PublicOrigin,
 [Parameter(Mandatory)][string]$RemoteHome,
 [Parameter(Mandatory)][string[]]$OAuthCallback,
 [string]$OutputDirectory=(Join-Path $env:USERPROFILE 'Jarvis-Secrets'))
$ErrorActionPreference='Stop'
if($PublicOrigin.Scheme -ne 'https' -or $PublicOrigin.AbsolutePath -ne '/' -or $PublicOrigin.Query -or $PublicOrigin.UserInfo){throw 'Use an HTTPS origin only.'}
if($RemoteHome -notmatch '^/home/[A-Za-z0-9_.-]+$'){throw 'Use the non-root deployment account home, e.g. /home/opc.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if(Test-Path (Join-Path $OutputDirectory 'server.private.json')){throw 'Refusing to overwrite existing keys/configuration.'}
$credential=Get-Credential -Message 'Initial Jarvis administrator (email and a strong password of at least 14 characters)'
$certs=@{}
foreach($name in 'Signing','Encryption'){
 $rsa=[System.Security.Cryptography.RSA]::Create(3072)
 try {
  $request=[System.Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=Jarvis OAuth $name",$rsa,[System.Security.Cryptography.HashAlgorithmName]::SHA256,[System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
  $usage=if($name -eq 'Signing'){[System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature}else{[System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment}
  $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($usage,$true))
  $certificate=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5),[DateTimeOffset]::UtcNow.AddYears(2))
  try {
   $password=[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
   [IO.File]::WriteAllBytes((Join-Path $OutputDirectory "$name.pfx"),$certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,$password))
   $certs["${name}Path"]="$RemoteHome/.config/jarvis-mcp-server/$name.pfx"; $certs["${name}Password"]=$password
  } finally {$certificate.Dispose()}
 } finally {$rsa.Dispose()}
}
$config=@{
 Jarvis=@{PublicOrigin=$PublicOrigin.AbsoluteUri.TrimEnd('/');DataDirectory="$RemoteHome/.local/share/jarvis-mcp-server/data";OAuthRedirectUris=$OAuthCallback;AllowRegistration=$true;AutoApproveRegistration=$false;ToolTimeoutSeconds=120}
 Bootstrap=@{AdminEmail=$credential.UserName;AdminPassword=$credential.GetNetworkCredential().Password}
 Certificates=$certs
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'server.private.json'),($config|ConvertTo-Json -Depth 6))
Write-Host 'Private configuration created. Protect this folder with user-only permissions and encrypted storage.'
Write-Host 'These are OAuth keys, NOT public HTTPS certificates. After successful bootstrap, remove Bootstrap from server.private.json.'
