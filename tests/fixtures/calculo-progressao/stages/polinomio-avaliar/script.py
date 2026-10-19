import numpy as np
from psrunner import stage


@stage
def run(inputs, ctx):
    coeficientes = np.array(inputs["coeficientes"], dtype=float)
    x = np.array(inputs["x"], dtype=float)
    valores = np.polynomial.polynomial.polyval(x, coeficientes)
    return {"valores": valores.tolist()}
