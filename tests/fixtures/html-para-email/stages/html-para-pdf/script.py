from psrunner import stage
from weasyprint import HTML


@stage
def run(inputs, ctx):
    pdf = ctx.new_file(inputs["nome_arquivo"], "application/pdf")
    document = HTML(filename=str(inputs["html"].path)).render()
    document.write_pdf(pdf.path)
    return {"pdf": pdf, "paginas": len(document.pages)}
