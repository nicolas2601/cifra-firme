# Nómina de demostración · `demo-v1`

Reglas de **demostración**, no una liquidación legal colombiana. Se aplican a una persona y un mes de calendario. El cálculo actual supone salario mensual positivo, en pesos enteros, y mes completo; no infiere asistencia ni prorratea altas o retiros. Las tasas son ficticias.

1. Base: salario mensual.
2. Ausencia: resta el valor de un día por fecha. Valor diario conceptual: salario dividido por los días calendario del mes.
3. Incapacidad: resta un tercio del valor diario; equivale a pagar dos tercios antes del redondeo.
4. Día adicional: suma el 125 % del valor diario. Un día laborado normal ya está cubierto por la base.
5. Contar los días primero. Redondear cada ajuste a pesos enteros, con empates alejados de cero; sumar los conceptos ya redondeados.

## Ejemplo: abril, 30 días

| Concepto | Cálculo | COP |
|---|---:|---:|
| Salario mensual | — | $1.800.000 |
| 2 ausencias | −2 × $60.000 | −$120.000 |
| 3 incapacidades | −3 × $60.000 ÷ 3 | −$60.000 |
| 1 día adicional | 1 × $60.000 × 1,25 | +$75.000 |
| **Total** | | **$1.695.000** |

Sin novedades se mantiene el salario; ausencias todo el mes dejan el total en cero. Febrero bisiesto usa 29 días. Una fecha no puede repetirse entre tipos ni pertenecer a otro mes. Se rechazan clases de novedad desconocidas y períodos que no comiencen el día 1.

La API liquida a personas activas con salario positivo. Si existen novedades de una persona inactiva o sin salario, el período no se cierra hasta resolverlas. Al cerrar se conservan la versión de reglas, el salario, los conceptos, el total y el detalle de novedades por persona. El CSV se genera desde ese histórico, no desde los datos del mes en curso. Un cierre significa que la liquidación se confirmó, no que se ordenó o ejecutó el pago.
