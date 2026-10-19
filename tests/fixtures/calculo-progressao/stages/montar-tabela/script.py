import pandas as pd
from psrunner import stage


@stage
def run(inputs, ctx):
    x, y = inputs["x"], inputs["y"]
    tabela = pd.DataFrame({"i": range(1, len(x) + 1), "x": x, "y": y})
    return {"tabela": tabela}
