#!/bin/sh
# Entrada das imagens dos hosts (spec 009): usa o psrunner embutido na imagem (D-169), a menos que
# PythonScriptRunner__PsrunnerRequirement já venha definido (por exemplo, um índice privado).
set -e
# Arquivos temporários (buffer de requisições do ASP.NET Core, spool de arquivos) vão para o volume de trabalho: a raiz
# pode ser somente leitura.
mkdir -p "${TMPDIR:-/tmp}"
if [ -z "${PythonScriptRunner__PsrunnerRequirement:-}" ]; then
  for wheel in /opt/psr/psrunner-*.whl; do
    PythonScriptRunner__PsrunnerRequirement="$wheel"
  done
  export PythonScriptRunner__PsrunnerRequirement
fi

exec "$@"
