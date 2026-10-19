import numpy as np
from psrunner import stage


@stage
def run(inputs, ctx):
    valores = np.array(inputs["valores"], dtype=float)
    return {
        "soma": float(valores.sum()),
        "media": float(valores.mean()),
        "desvio_padrao": float(valores.std()),
    }
