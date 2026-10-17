param(
    [ValidateSet('IndicatorGlass', 'IndicatorPrism')]
    [string[]] $Shader = @('IndicatorPrism')
)

$ErrorActionPreference = 'Stop'
if (-not ('BetterFlowShaderCompiler' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class BetterFlowShaderCompiler
{
    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] UIntPtr GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(
        byte[] source, UIntPtr length, [MarshalAs(UnmanagedType.LPStr)] string sourceName,
        IntPtr defines, IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entry,
        [MarshalAs(UnmanagedType.LPStr)] string target, uint flags, uint effectFlags,
        out ID3DBlob code, out ID3DBlob errors);

    public static void Compile(string sourcePath, string outputPath)
    {
        byte[] source = File.ReadAllBytes(sourcePath);
        ID3DBlob code = null, errors = null;
        try
        {
            int result = D3DCompile(source, (UIntPtr)source.Length, sourcePath,
                IntPtr.Zero, IntPtr.Zero, "main", "ps_3_0", 0x8000, 0, out code, out errors);
            if (result < 0)
                throw new InvalidOperationException(errors == null ? "Shader compilation failed." :
                    Marshal.PtrToStringAnsi(errors.GetBufferPointer()));
            var bytes = new byte[checked((int)code.GetBufferSize().ToUInt64())];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
            File.WriteAllBytes(outputPath, bytes);
        }
        finally
        {
            if (code != null) Marshal.FinalReleaseComObject(code);
            if (errors != null) Marshal.FinalReleaseComObject(errors);
        }
    }
}
'@
}

$assetDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets'
foreach ($shaderName in $Shader) {
    $sourcePath = Join-Path $assetDirectory "$shaderName.hlsl"
    $outputPath = Join-Path $assetDirectory "$shaderName.ps"
    [BetterFlowShaderCompiler]::Compile($sourcePath, $outputPath)
    Get-Item -LiteralPath $outputPath | Select-Object Name, Length
}
