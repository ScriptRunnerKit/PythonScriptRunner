import smtplib
import ssl
from datetime import datetime, timezone
from email.message import EmailMessage
from email.utils import make_msgid

from psrunner import stage


@stage
def run(inputs, ctx):
    remetente = inputs["remetente"]
    mensagem = EmailMessage()
    mensagem["From"] = remetente
    mensagem["To"] = ", ".join(inputs["destinatarios"])
    mensagem["Subject"] = inputs["assunto"]
    message_id = make_msgid(domain=remetente.rsplit("@", 1)[-1])
    mensagem["Message-ID"] = message_id
    mensagem.set_content(inputs["corpo"])

    anexo = inputs.get("anexo")
    if anexo is not None:
        tipo, _, subtipo = anexo.content_type.partition("/")
        mensagem.add_attachment(anexo.read_bytes(), maintype=tipo, subtype=subtipo or "octet-stream", filename=anexo.name)

    host, porta = inputs["smtp_host"], inputs["smtp_porta"]
    if porta == 465:
        servidor = smtplib.SMTP_SSL(host, porta, context=ssl.create_default_context(), timeout=30)
    else:
        servidor = smtplib.SMTP(host, porta, timeout=30)

    with servidor:
        if porta != 465 and servidor.has_extn("starttls"):
            servidor.starttls(context=ssl.create_default_context())
        servidor.login(ctx.secrets["SMTP_USUARIO"], ctx.secrets["SMTP_SENHA"])
        servidor.send_message(mensagem)

    ctx.log.info("e-mail %s enviado para %d destinatário(s)", message_id, len(inputs["destinatarios"]))
    return {"message_id": message_id, "enviado_em": datetime.now(timezone.utc)}
