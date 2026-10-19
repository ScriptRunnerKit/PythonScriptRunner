from psrunner import stage


@stage
def run(inputs, ctx):
    a1, razao, n = inputs["a1"], inputs["razao"], inputs["n"]
    if inputs["tipo"] == "geometrica":
        termos = [a1 * razao**i for i in range(n)]
    else:
        termos = [a1 + razao * i for i in range(n)]
    ctx.log.info("gerados %d termos", n)
    return {"termos": termos}
